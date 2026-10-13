using Synqra.AppendStorage;

namespace Synqra.Projection.InMemory;

/// <summary>
/// Runs a <see cref="BatchCommand"/> against a scratch projection rebuilt from the stream's event log,
/// so every child is fully validated — existence, uniqueness, vetoes, link dedup, and the effects of
/// earlier children — before anything touches the real projection or the log. Returns the events the
/// children produced, ready to be applied and stored as one unit. Any refusal throws, and the caller
/// has changed nothing. Backend-agnostic: the real projection only ever applies already-validated
/// events, the same way it applies events arriving over replication.
/// </summary>
public sealed class BatchRunner(IAppendStorage<Event, Guid> log, ISynqraIdProvider ids, Func<InMemoryProjection> createScratch)
{
	/// <summary>Throws <see cref="ConcurrencyException"/> for the first target no longer at its expected version.</summary>
	public void CheckExpectations(BatchCommand batch, Func<BatchExpectation, Guid> currentVersion)
	{
		foreach (var expectation in batch.Expectations)
		{
			if (expectation.ExpectedLastEventId == Guid.Empty)
			{
				continue;
			}
			var actual = currentVersion(expectation);
			if (actual != expectation.ExpectedLastEventId)
			{
				throw new ConcurrencyException(expectation.TargetId, expectation.ExpectedLastEventId, actual);
			}
		}
	}

	public async Task<IReadOnlyList<Event>> DryRunAsync(BatchCommand batch, Guid streamId)
	{
		var scratch = createScratch();
		// The log is multitenant and has no per-stream range read yet, so this is a scan of the whole
		// log (every stream) per batch; it runs inside the projection's submit gate. A per-stream read
		// in the storage, or an overlay on the live projection, is the known next step.
		await foreach (var ev in log.GetAllAsync(from: default))
		{
			if (ev.StreamId != default && ev.StreamId != streamId)
			{
				continue;
			}
			if (ev is CommandCreatedEvent)
			{
				// Commands carry live instances and change no domain state: nothing to validate against.
				continue;
			}
			// The log can hand back the very instances the real projection holds; the scratch gets its own.
			await scratch.ApplyAsync(Detached(ev), isReplay: true);
		}
		var produced = new List<Event>();
		scratch.EventsCommitted += events => produced.AddRange(events);
		// The scratch attaches every new instance it materializes. It gets copies, and the produced
		// events carry the caller's detached originals, so the real projection can attach those.
		// Copies, not clones: a clone would share the original's component collection with the scratch.
		var originals = new Dictionary<Guid, object>();
		foreach (var child in batch.Commands)
		{
			if (child is BatchCommand)
			{
				throw new NotSupportedException("A batch cannot contain another batch.");
			}
			if (child is SingleObjectCommand { TargetObject: not null, })
			{
				throw new NotSupportedException("Commands in a batch address their target by id; TargetObject is not supported.");
			}
			if (child.StreamId == default)
			{
				child.StreamId = streamId;
			}
			if (child.CommandId == default)
			{
				child.CommandId = ids.CreateCommandId(SynqraIdProviderExtensions.DeclaredTypeId(child.GetType()));
			}
			switch (child)
			{
				case AddComponentCommand { Data: { } data, } add when data is not System.Collections.IDictionary:
					originals[child.CommandId] = data;
					add.Data = DetachedCopy(data);
					break;
				case AddLinkCommand { Data: { } data, } link when data is not System.Collections.IDictionary:
					originals[child.CommandId] = data;
					link.Data = DetachedCopy(data);
					break;
			}
			try
			{
				await scratch.SubmitCommandAsync(child);
			}
			finally
			{
				// The batch's own commands keep the caller's instances whatever happens.
				if (originals.TryGetValue(child.CommandId, out var original))
				{
					switch (child)
					{
						case AddComponentCommand add:
							add.Data = original;
							break;
						case AddLinkCommand link:
							link.Data = original;
							break;
					}
				}
			}
		}
		foreach (var ev in produced)
		{
			if (originals.TryGetValue(ev.CommandId, out var original))
			{
				switch (ev)
				{
					case ComponentAddedEvent added:
						added.Data = original;
						break;
					case LinkAddedEvent linked:
						linked.Data = original;
						break;
				}
			}
		}
		return produced;
	}

	readonly System.Reflection.MethodInfo _memberwiseClone =
		typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

	/// <summary>The event as the scratch should see it: same facts, its own model instance.</summary>
	Event Detached(Event ev)
	{
		switch (ev)
		{
			case ComponentAddedEvent { Data: { } data, } when data is not System.Collections.IDictionary:
			{
				var copy = (ComponentAddedEvent)_memberwiseClone.Invoke(ev, null)!;
				copy.Data = DetachedCopy(data);
				return copy;
			}
			case LinkAddedEvent { Data: { } data, } when data is not System.Collections.IDictionary:
			{
				var copy = (LinkAddedEvent)_memberwiseClone.Invoke(ev, null)!;
				copy.Data = DetachedCopy(data);
				return copy;
			}
			default:
				return ev;
		}
	}

	/// <summary>
	/// A fresh, unattached instance with the original's settable values and component id. Collections
	/// it owns (components) start empty, and references to other models are left out, so nothing the
	/// scratch does reaches the original.
	/// </summary>
	object DetachedCopy(object data)
	{
		var type = data.GetType();
		var copy = Activator.CreateInstance(type, nonPublic: true)
			?? throw new NotSupportedException($"{type.FullName} has no parameterless constructor; it cannot be validated in a batch.");
		foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
		{
			if (!property.CanRead || property.SetMethod is not { IsPublic: true, } || property.GetIndexParameters().Length != 0)
			{
				continue;
			}
			var value = property.GetValue(data);
			if (value is IBindableModel)
			{
				continue;
			}
			property.SetValue(copy, value);
		}
		if (copy is IBindableComponent bindable && data is IIdentifiable<Guid> identified)
		{
			bindable.SetComponentId(identified.Id);
		}
		return copy;
	}
}
