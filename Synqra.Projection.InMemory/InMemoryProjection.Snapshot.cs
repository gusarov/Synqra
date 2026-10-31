using Microsoft.Extensions.Logging;
using Synqra.BinarySerializer;
using Synqra.Projection;

namespace Synqra;

// Stream snapshots (plans/snapshot.md): a client folds the snapshot its log starts with; a master built on this projection cuts one.
public partial class InMemoryProjection
{
	/// <summary>
	/// Folds a stream snapshot the client keeps in its log: every record is added as its entity, facet or link, as a replay
	/// (no activator runs), roots first, so a facet or a link always finds its owner. An entity's version is the one it had on
	/// the master.
	/// </summary>
	public async Task VisitAsync(StreamSnapshotEvent ev, EventVisitorContext ctx)
	{
		var reader = new SnapshotChunkReader(new SnapshotSchema(_serializerFactory).CreateReader(ev.Header));
		var owners = new Dictionary<Guid, (TypeMetadata Type, Guid Version)>();
		var replay = new EventVisitorContext { IsReplay = true };
		var applied = 0L;
		foreach (var chunk in ev.Chunks.OrderBy(x => x.Index))
		{
			foreach (var record in reader.Read(chunk))
			{
				var type = TypeMetadataProvider.GetTypeMetadata(record.Data.GetType());
				if (record.IsRoot)
				{
					owners[record.ComponentId] = (type, record.Version ?? ev.EventId);
				}
				if (owners.TryGetValue(record.Owner ?? record.ComponentId, out var owner))
				{
					// Applied in place, not through AcceptAsync: the records are not events of the log, and only the snapshot moves the cursor.
					await VisitAsync(new ComponentAddedEvent
					{
						EventId = owner.Version,
						CommandId = ev.CommandId,
						StreamId = StreamId,
						TargetId = record.Owner ?? record.ComponentId,
						TargetTypeId = owner.Type.TypeId,
						CollectionId = owner.Type.CollectionId,
						ComponentTypeId = type.TypeId,
						ComponentId = record.ComponentId,
						Data = record.Data,
					}, replay);
					applied++;
				}
				else
				{
					EmergencyLog.Default.LogError($"InMemoryProjection: snapshot {ev.Header.SnapshotId} of stream {StreamId} holds {record}, whose owner it does not hold; stepped over.");
				}
			}
		}
		EmergencyLog.Default.Stage($"projection {StreamId}: snapshot at {ev.Header.Watermark} applied, {applied} records, {owners.Count} entities");
	}

	/// <summary>
	/// The stream's state at this projection's cursor (plans/snapshot.md §4), for a master that serves snapshots from its
	/// in-memory projection: the log is caught up first and the records are copied, all under the write gate, so no write lands
	/// in between and later writes do not reach the copies. Null when the stream holds no entity.
	/// </summary>
	public Task<SnapshotCut?> CutAsync(CancellationToken cancellationToken = default)
		=> _gate.RunAsync(async () =>
		{
			if (_eventStorage is not null)
			{
				var log = _eventStorage;
				await CatchUpAsync(after => log.ReadStreamAsync(StreamId, after, cancellationToken), isReplay: Cursor == default, cancellationToken);
			}
			var records = CopyRecords();
			return records.Count == 0 ? null : new SnapshotCut(Cursor, records.ToAsyncEnumerable());
		});

	// Roots first, then each owner's facets and links: a record never arrives before the entity it hangs on.
	List<SnapshotRecord> CopyRecords()
	{
		var copier = new DataCopier(_serializerFactory.CreateSerializer());
		var roots = new List<SnapshotRecord>();
		var hanging = new List<SnapshotRecord>();
		foreach (var (id, reference) in _attachedObjectsById.OrderBy(x => x.Key))
		{
			var model = reference.Target;
			if (model is not null and not Command && _attachedObjects.TryGetValue(model, out var attached) && attached is not null)
			{
				roots.Add(new SnapshotRecord
				{
					ComponentId = id,
					Version = attached.LastEventId == default ? null : attached.LastEventId,
					Data = copier.Copy(model),
				});
				if (model is IComponentContainer container)
				{
					foreach (var facet in container.Components)
					{
						if (facet is not Link && facet is IIdentifiable<Guid> identified)
						{
							hanging.Add(new SnapshotRecord { ComponentId = identified.Id, Owner = id, Data = copier.Copy(facet), });
						}
					}
				}
			}
		}
		foreach (var link in _linksById.Values)
		{
			hanging.Add(new SnapshotRecord { ComponentId = link.LinkId, Owner = link.SourceId, Data = copier.Copy(link), });
		}
		roots.AddRange(hanging.OrderBy(x => x.Owner).ThenBy(x => x.ComponentId));
		return roots;
	}

	/// <summary>A detached copy of a live model: written and read back through SBX, so the copy holds exactly what a snapshot sends.</summary>
	sealed class DataCopier(ISbxSerializer serializer)
	{
		byte[] _buffer = new byte[16 * 1024];

		public object Copy(object data)
		{
			object? copy = null;
			while (copy is null)
			{
				var written = 0;
				if (TryWrite(data, ref written))
				{
					var read = 0;
					serializer.Reset();
					copy = serializer.Deserialize<object>(_buffer.AsSpan(0, written), ref read)
						?? throw new InvalidOperationException($"InMemoryProjection: a {data.GetType().Name} read back as nothing.");
				}
				else
				{
					_buffer = new byte[_buffer.Length * 2];
				}
			}
			return copy;
		}

		bool TryWrite(object data, ref int pos)
		{
			var written = false;
			serializer.Reset();
			try
			{
				serializer.Serialize<object>(_buffer.AsSpan(), ref pos, data);
				written = true;
			}
			// By type, never through an exception filter: WASM AOT traps in a filter of a generic serializer path (#190).
			catch (SbxBufferTooSmallException)
			{
			}
			catch (IndexOutOfRangeException)
			{
			}
			catch (ArgumentOutOfRangeException)
			{
			}
			return written;
		}
	}
}

/// <summary>
/// Serves snapshots from the in-memory projections of an in-memory master (Contoso, the test simulator): the stream's projection
/// from the provider, cut at its cursor.
/// </summary>
public sealed class InMemoryStreamSnapshotSource(IProjectionProvider provider) : IStreamSnapshotSource
{
	public async Task<SnapshotCut?> CutAsync(Guid streamId, CancellationToken cancellationToken = default)
		=> await provider.GetAsync(streamId, cancellationToken) is InMemoryProjection projection
			? await projection.CutAsync(cancellationToken)
			: null;
}
