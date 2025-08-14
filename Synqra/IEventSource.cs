using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Synqra;

/// <summary>
/// Low-level storage interface for storyng and retrieving events
/// </summary>
public interface IStorage : IDisposable, IAsyncDisposable
{
	Task AppendAsync<T>(T item);

	IAsyncEnumerable<T> GetAll<T>();

	[EditorBrowsable(EditorBrowsableState.Advanced)]
	Task FlushAsync() => Task.CompletedTask;
}

internal class AttachedData
{
	public string? TrackingSinceJsonSnapshot { get; set; }
}


public static class SynqraExtensions
{
	public static IHostApplicationBuilder AddSynqraStoreContext(this IHostApplicationBuilder builder)
	{
		// builder.Services.AddSingleton<StoreContext>();
		// builder.Services.AddSingleton<IStoreContext>(sp => sp.GetRequiredService<StoreContext>());
		builder.Services.AddSingleton<ISynqraStoreContext, StoreContext>();
		// builder.Services.AddSingleton(typeof(IStoreCollection<>), (sp, s) => sp.GetRequiredService<IStoreContext>().Get<>); // Example storage implementation
		return builder;
	}
}


public static class SynqraPocoTrackingExtensions
{
	class ExtendingEntry
	{
		public string? TrackingSinceJsonSnapshot { get; set; }
	}

	public interface ITrackingSession : IDisposable, IAsyncDisposable
	{
		void Add(object item);
	}

	private sealed class TrackingSessionImplementation : ITrackingSession
	{
		private readonly StoreCollection _storeCollection;

		Dictionary<object, string> _originalsSerialized = new();

		public TrackingSessionImplementation(StoreCollection storeCollection, IEnumerable<object> items)
		{
			_storeCollection = storeCollection;

			lock (_originalsSerialized)
			{
				foreach (var item in items)
				{
					AddCore(item);
				}
			}
		}

		public void Add(object item)
		{
			lock (_originalsSerialized)
			{
				AddCore(item);
			}
		}

		void AddCore(object item)
		{
			_storeCollection.Store.GetId(item, null, GetMode.RequiredId); // ensure attached
			_originalsSerialized[item] = JsonSerializer.Serialize(item, _storeCollection.Type, _storeCollection.Store._jsonSerializerContext.Options);
		}

		public void Dispose()
		{
			// compare and submit changes
			lock (_originalsSerialized)
			{
				foreach (var kvp in _originalsSerialized)
				{
					// serialzie again
					var json = JsonSerializer.Serialize(kvp.Key, _storeCollection.Type, ((StoreContext)_storeCollection.Store)._jsonSerializerContext.Options);
					if (json != kvp.Value)
					{
						// changed!!
						var original = JsonSerializer.Deserialize<IDictionary<string, object?>>(kvp.Value, ((StoreContext)_storeCollection.Store)._jsonSerializerContext.Options);
						var updated = JsonSerializer.Deserialize<IDictionary<string, object?>>(json, ((StoreContext)_storeCollection.Store)._jsonSerializerContext.Options);
						foreach (var item in original.Keys.Union(updated.Keys))
						{
							var oldValue = original.TryGetValue(item, out var ov) ? ov : null;
							var newValue = updated.TryGetValue(item, out var nv) ? nv : null;
							if (oldValue != newValue)
							{
								_storeCollection.Store.SubmitCommandAsync(new ChangeObjectPropertyCommand
								{
									ContainerId = _storeCollection.ContainerId,
									CommandId = GuidExtensions.CreateVersion7(),
									TargetTypeId = ((StoreContext)_storeCollection.Store).GetTypeMetadata(_storeCollection.Type).TypeId,
									PropertyName = item,
									OldValue = oldValue,
									NewValue = newValue,
									TargetId = _storeCollection.Store.GetId(kvp.Key, null, GetMode.RequiredId),
								}).GetAwaiter().GetResult();
							}
						}
					}
				}
			}
		}

		public async ValueTask DisposeAsync()
		{
			Dispose();
		}
	}

	// private static readonly ConditionalWeakTable<object, ExtendingEntry> _attachedProperties = new();

	public static ITrackingSession PocoTracker(this ISynqraCollection collection, params IEnumerable<object> items)
	{
		/*
		var attached = _attachedProperties.GetOrCreateValue(collection);
		if (!ReferenceEquals(null, attached.TrackingSinceJsonSnapshot))
		{
			throw new Exception("Previous tracker is not closed!");
		}
		*/
		// ((IStoreCollectionInternal)collection).Store.
		return new TrackingSessionImplementation((StoreCollection)collection, items);
		// CollectionsMarshal.GetValueRefOrAddDefault(((IStoreCollectionInternal)collection)._attachedObjects, q.GetId(), out var exists);
	}
}

public interface ICommandVisitor<T>
{
	Task BeforeVisitAsync(Command cmd, T ctx);
	Task AfterVisitAsync(Command cmd, T ctx);

	Task VisitAsync(CreateObjectCommand cmd, T ctx);
	Task VisitAsync(DeleteObjectCommand cmd, T ctx);
	Task VisitAsync(ChangeObjectPropertyCommand cmd, T ctx);

	/*
	Task VisitAsync(MoveNode cmd, T ctx);
	Task VisitAsync(MarkAsDone cmd, T ctx);
	Task VisitAsync(RevertCommand cmd, T ctx);
	Task VisitAsync(PrePopulate cmd, T ctx);
	Task VisitAsync(ChangeSetting cmd, T ctx);
	Task VisitAsync(BatchCommand cmd, T ctx);
	Task VisitAsync(ChangeDependantNode cmd, T ctx);
	Task VisitAsync(AddComponent cmd, T ctx);
	Task VisitAsync(ChangeComponentProperty cmd, T ctx);
	Task VisitAsync(DeleteComponent cmd, T ctx);
	*/
}

class CommandHandlerContext
{
	public List<Event> Events { get; internal set; } = new List<Event>();
}

class EventVisitorContext
{
}

class TypeMetadata
{
	public Type Type { get; set; }
	public Guid TypeId { get; set; }

	public override string ToString()
	{
		return $"{TypeId.ToString("N")[..4]} {Type.Name}";
	}
}
