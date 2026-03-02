using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra.Projection.File;

public static class FileSynqraExtensions
{
	const int BufferSizeForObject = 10240;

	static FileSynqraExtensions()
	{
		// AOT ROOTS:
		_ = typeof(IAppendStorage<Event, Guid>);
	}

	public static IHostApplicationBuilder AddFileSynqraStore(this IHostApplicationBuilder builder)
	{
		builder.Services.AddSingleton<AppendStores>();
		builder.Services.AddSingleton<FileObjectStore>();
		builder.Services.AddSingleton<FileProjection>();
		builder.Services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<FileObjectStore>());
		builder.Services.AddSingleton<IProjection>(sp => sp.GetRequiredService<FileProjection>());
		return builder;
	}

	private static Guid GetId(this IObjectStore ctx, object model, FileObjectCollection? collection, GetMode mode)
	{
		return ((FileObjectStore)ctx).GetId(model, collection, mode);
	}

	private static AttachedObjectData Attach(this IObjectStore ctx, object model, FileObjectCollection collection)
	{
		return ((FileObjectStore)ctx).Attach(model, collection);
	}

	private static (bool IsJustCreated, Guid Id) GetOrCreateId(this IObjectStore ctx, object model, FileObjectCollection collection)
	{
		return ((FileObjectStore)ctx).GetOrCreateId(model, collection);
	}

	private class AttachedObjectData
	{
		public required Guid Id { get; init; }
		public required FileObjectCollection Collection { get; init; }
		public required bool IsJustCreated { get; set; }
	}

	// It is not flags, as all possible permutations are defined explicitly
	private enum GetMode : byte
	{
		// 0b_0000_0000
		//          MME
		// E - Behavior for existing object (0 - throw, 1 - return)
		// MM - Behavior for missing object (0 - throw, 1 - zero_default, 2 - create_id)

		// 0b_MM_E
		Invalid,     // 00 0
		RequiredId,  // 00 1
		MustAbsent,  // 01 0
		TryGet,      // 01 1
		RequiredNew, // 10 0
		GetOrCreate, // 10 1
	}

	private class FileObjectStoreConfig
	{
		// in future container is sort of "database". For multitanent storage, every user has a set of his own containers, e.g. "settings", "nodes"
		// - user1 // container (like sql database filtered by user)
		// - user2 // container (like sql database filtered by user)
		//   - collection type: "node" collectionName: "" // main collection
		//   - collection type: "node" collectionName: "archive" // additional named collection
		//   - collection "userSettings"
		public Guid ContainerId { get; set; } = SynqraGuids.SynqraRootContainerId; // for current phase this is global root container, no distinction yet, just to pass validations
	}

	private class FileObjectStore : IObjectStore
	{
		private readonly Dictionary<Guid, FileObjectCollection> _collections = new Dictionary<Guid, FileObjectCollection>();
		private readonly ConditionalWeakTable<object, AttachedObjectData> _attachedObjects = new ConditionalWeakTable<object, AttachedObjectData>();
		private readonly ConcurrentDictionary<Guid, WeakReference> _attachedObjectsById = new();

		private byte _attachedMaintain;
		public ISBXSerializerFactory SerializerFactory { get; }
		private readonly Lazy<IProjection> _lazyFileProjection;
		private readonly IOptions<FileObjectStoreConfig> _options;

		private FileProjection _fileProjection => (FileProjection)_lazyFileProjection.Value;

		public Guid ContainerId { get; } = SynqraGuids.SynqraRootContainerId; // for current phase this is global root container, no distinction yet, just to pass validations

		public ITypeMetadataProvider TypeMetadataProvider { get; }

		public AppendStores AppendStores { get; }

		public GuidExtensions.Generator GuidGenerator { get; }

		public FileObjectStore(
			  ITypeMetadataProvider typeMetadataProvider
			, ISBXSerializerFactory serializerFactory
			, Lazy<IProjection> fileProjection
			, IOptions<FileObjectStoreConfig> options
			, AppendStores appendStores
			, GuidExtensions.Generator? generator = null
			)
		{
			TypeMetadataProvider = typeMetadataProvider;
			SerializerFactory = serializerFactory;
			_lazyFileProjection = fileProjection;
			_options = options;
			AppendStores = appendStores;
			ContainerId = options.Value.ContainerId;
			GuidGenerator = generator ?? new GuidExtensions.Generator();
		}

		private void EventuallyMaintain()
		{
			if (++_attachedMaintain == 0 || Random.Shared.Next(1024) == 0)
			{
				foreach (var deadKey in _attachedObjectsById.Where(x => !x.Value.IsAlive).Select(x => x.Key))
				{
					_attachedObjectsById.TryRemove(deadKey, out _);
				}
			}
		}

		ISynqraCollection IObjectStore.GetCollection(Type type, string? collectionName)
		{
			return GetCollection(type, collectionName ?? "");
		}

		internal FileObjectCollection GetCollection(Type type, string collectionName = "")
		{
			var metadata = TypeMetadataProvider.GetTypeMetadata(type);
			var collectionId = metadata.GetCollectionId(collectionName ?? throw new ArgumentNullException(nameof(collectionName)));

			return GetCollection(type, collectionId);
		}

		ISynqraCollection<T> IObjectStore.GetCollection<T>(string? collectionName) where T : class
		{
			return GetCollection<T>(collectionName ?? "");
		}

		internal FileObjectCollection<T> GetCollection<T>(string collectionName = "") where T : class
		{
			var metadata = TypeMetadataProvider.GetTypeMetadata(typeof(T));
			var collectionId = metadata.GetCollectionId(collectionName);
			return GetCollection<T>(collectionId);
		}

		internal FileObjectCollection GetCollection(Type type, Guid collectionId)
		{
#if NET7_0_OR_GREATER
			ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_collections, collectionId, out _);
			if (slot == null)
			{
				var gtype = typeof(FileObjectCollection<>).MakeGenericType(type);
				slot = (FileObjectCollection)Activator.CreateInstance(gtype, [
				  /* store */ this
				, /* containerId */ ContainerId
				, /* collectionId */ collectionId
				, /* serializerFactory */ SerializerFactory
				])!;
			}
			return slot;
#else
			throw new Exception("Not implemented for older frameworks");
#endif
		}

		internal FileObjectCollection<T> GetCollection<T>(Guid collectionId) where T : class
		{
#if NET7_0_OR_GREATER
			ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_collections, collectionId, out _);
			if (slot == null)
			{
				var col = new FileObjectCollection<T>(
				  /* store */ this
				, /* containerId */ ContainerId
				, /* collectionId */ collectionId
				, /* serializerFactory */ SerializerFactory
				);
				slot = col;
				return col;
			}
			return (FileObjectCollection<T>)slot;
#else
			throw new Exception("Not implemented for older frameworks");
#endif
		}

		public Guid GetId(object model)
		{
			return GetId(model, null, GetMode.RequiredId);
		}

		internal Guid GetId(object model, FileObjectCollection? collection, GetMode mode)
		{
#if NET8_0_OR_GREATER
			if (_attachedObjects.TryGetValue(model, out var attachedData) && attachedData is not null)
			{
				if (((byte)mode & 1) == 0)
				{
					throw new Exception("Object already have id assigned.");
				}
				return attachedData.Id;
			}
			else
			{
				switch ((byte)mode >> 1)
				{
					case 0:
						throw new InvalidOperationException($"Object is not attached to the store context.");
					case 1:
						return default; // return Guid.Empty
					case 2:
						if (_attachedObjects.TryAdd(model, attachedData = new AttachedObjectData
						{
							Id = GuidExtensions.CreateVersion7(),
							IsJustCreated = false,
							Collection = collection ?? throw new Exception("Collection is not specified for new object"),
						}))
						{
							EventuallyMaintain();
							_attachedObjectsById[attachedData.Id] = new WeakReference(model);
						}
						;
						return attachedData.Id;
					default:
						throw new IndexOutOfRangeException($"Unknown mode <{mode}>");
				}
			}
			// throw new InvalidOperationException($"The object {model} is not attached to the store context.");
#else
		throw new Exception("Not implemented for older frameworks");
#endif
		}

		internal AttachedObjectData Attach(object model, FileObjectCollection collection)
		{
			var data = GetAttachedData(model, default, collection, GetMode.RequiredNew);
			// data.Id
			return data;
		}

		internal object? GetAttachedObject(Guid id, FileObjectCollection? collection = null)
		{
			EventuallyMaintain();
			if (_attachedObjectsById.TryGetValue(id, out var weakRef))
			{
				var target = weakRef.Target;
				if (target != null)
				{
					return target;
				}
			}
			return null;
		}

		internal AttachedObjectData GetAttachedData(object model, Guid id, FileObjectCollection? collection, GetMode mode)
		{
			if (model == null)
			{
				throw new ArgumentNullException(nameof(model));
			}
#if NET8_0_OR_GREATER
			if (_attachedObjects.TryGetValue(model, out var attachedData) && attachedData is not null)
			{
				if (((byte)mode & 1) == 0)
				{
					throw new Exception("Object already have id assigned.");
				}
				attachedData.IsJustCreated = false;
				if (id != default && attachedData.Id != id)
				{
					throw new InvalidOperationException($"Object is already attached with different id <{attachedData.Id}>. Expected <{id}>.");
				}
				if (collection != default && attachedData.Collection != collection)
				{
					throw new InvalidOperationException($"Object is already attached with different collection <{collection}>. Expected <{collection}>.");
				}
				return attachedData;
			}
			else
			{
				switch ((byte)mode >> 1)
				{
					case 0:
						throw new InvalidOperationException($"Object is not attached to the store context.");
					case 1:
						return null!; // return null
					case 2:
						if (id == default)
						{
							id = GuidGenerator.CreateVersion7();
						}
						if (collection is null)
						{
							throw new Exception("Can not attach object without collection");
						}
						if (model is IBindableModel bm)
						{
							if (bm.Store != null)
							{
								if (bm.Store != this)
								{
									throw new Exception("The model is already attached to store. To Different store.");
								}
								else
								{
									throw new Exception("The model is already attached to store. It is same store but still, inconsistent.");
								}
							}
							bm.Attach(this, collection.CollectionId);
						}
						EventuallyMaintain();
						if (_attachedObjectsById.TryGetValue(id, out var wr))
						{
							var target = wr.Target;
							if (target != null)
							{
								throw new Exception("This id is already used in the store. Pass default to generate new or make sure your id is fresh indeed");
							}
						}

						if (_attachedObjects.TryAdd(model, attachedData = new AttachedObjectData
						{
							Id = id,
							IsJustCreated = true,
							Collection = collection,
						}))
						{
						}
						;
						_attachedObjectsById[id] = new WeakReference(model);
						return attachedData;
					default:
						throw new IndexOutOfRangeException($"Unknown mode <{mode}>");
				}
			}
#else
		throw new Exception("Not implemented for older frameworks");
#endif
		}

		public async Task SubmitCommandAsync(ISynqraCommand newCommand)
		{
			await _fileProjection.ProcessCommandAsync(newCommand);
		}
	}

	private abstract class FileObjectCollection : StoreCollection, ISynqraCollection
	{
		protected internal FileObjectStore _store;

		protected FileObjectCollection(
			  FileObjectStore store
			, Guid containerId
			, Guid collectionId
			, ISBXSerializerFactory serializerFactory
			) : base(
			  store
			, containerId
			, collectionId
			, serializerFactory
			)
		{
			_store = store;
		}

		public object this[Guid index]
		{
			get
			{
				var enumerable = _store.AppendStores.ItemAppendStorage.GetAllAsync((CollectionId, index)).ToBlockingEnumerable();
				var first = enumerable.FirstOrDefault();
				if (first != null)
				{
					var blob = ConsumeItem(index, first);
					var id = _store.GetId(blob);
					if (id == index)
					{
						return blob;
					}
				}
				throw new Exception($"Object with id {index} is not found in collection {CollectionId}");
			}
		}

		// Convert item into object (and maintain cache if needed)
		internal object ConsumeItem(Guid index, Item item)
		{
			var attached = _store.GetAttachedObject(index, this);
			if (attached != null)
			{
				return attached;
			}
			_store.GetAttachedData(item.Blob, index, this, GetMode.GetOrCreate); // attach object to store and cache it
			return item.Blob;
		}

		// public abstract IEnumerator GetEnumerator();
	}

	private class FileObjectCollection<T> : FileObjectCollection, ISynqraCollection<T>
		where T : class
	{

		public FileObjectCollection(
			  FileObjectStore store
			, Guid containerId
			, Guid collectionId
			, ISBXSerializerFactory serializerFactory
			) : base(
			  store
			, containerId
			, collectionId
			, serializerFactory
			)
		{
		}

		public override Type Type => typeof(T);

		public int Count
		{
			get
			{
				var cnt = 0;
				// TODO super lame, use directories at least, not materialized instances
				foreach (var item in this)
				{
					cnt++;
				}
				return cnt;
			}
		}

		public bool IsReadOnly => throw new NotImplementedException();

		public new T this[Guid index]
		{
			get
			{
				var item = base[index];
				return (T)item;
			}
		}

		// Client request - generate command
		private int Add(T item)
		{
			// var o = _list.Count;

			// var dataJson = _jsonSerializerOptions == null ? null : JsonSerializer.Serialize(item, _jsonSerializerOptions);
			// var data = _jsonSerializerOptions == null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson, _jsonSerializerOptions);

			var attachedData = _store.Attach(item, this);
			var task = _store.SubmitCommandAsync(new CreateObjectCommand
			{
				ContainerId = _store.ContainerId,
				CollectionId = CollectionId,
				TargetTypeId = _store.TypeMetadataProvider.GetTypeMetadata(typeof(T)).TypeId,
				CommandId = _store.GuidGenerator.CreateVersion7(), // This is a new object, so we generate a new command Id
				TargetId = attachedData.Id,
				Data = item,
				TargetObject = item,
			});
			if (!OperatingSystem.IsBrowser())
			{
				task.GetAwaiter().GetResult();
				// var n = _list.Count;
				// return n == o ? n + 1 : n; // if it is not changed, then it will be next index, if updated, then new count is actual index
			}
			return int.MinValue;
		}

		void ICollection<T>.Add(T item)
		{
			Add(item);
		}

		public void Clear()
		{
			throw new NotImplementedException();
		}

		public bool Contains(T item)
		{
			throw new NotImplementedException();
		}

		public void CopyTo(T[] array, int arrayIndex)
		{
			foreach (var item in this)
			{
				array[arrayIndex++] = item;
			}
		}

		public override IEnumerator<T> GetEnumerator()
		{
			return new Enumerator(this);
		}

		public bool Remove(T item)
		{
			throw new NotImplementedException();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		public class Enumerator : IEnumerator<T>
		{
			private readonly FileObjectCollection _fileObjectCollection;
			private IEnumerable<Item> _enumerable;
			private IEnumerator<Item> _enumerator;

			public Enumerator(FileObjectCollection fileObjectCollection)
			{
				_fileObjectCollection = fileObjectCollection;
				_enumerable = _fileObjectCollection._store.AppendStores.ItemAppendStorage.GetAllAsync((fileObjectCollection.CollectionId, default)).ToBlockingEnumerable();
				Reset();
			}
			public T Current
			{
				get
				{
					var item = _enumerator.Current;
					var obj = _fileObjectCollection.ConsumeItem(item.ObjectId, item);

					return (T)obj;
				}
			}
			object IEnumerator.Current => Current;
			public void Dispose()
			{
			}
			public bool MoveNext()
			{
				var moved = _enumerator.MoveNext();
				return moved;
			}
			public void Reset()
			{
				_enumerator?.Dispose();
				_enumerator = _enumerable.GetEnumerator();
			}
		}
	}

	private class FileProjection(
		  ITypeMetadataProvider _typeMetadataProvider
		, Lazy<IObjectStore> _lazyObjectStore
		, AppendStores _appendStores
		, IAppendStorage<Event, Guid> _eventStorage
		, IEventReplicationService? _eventReplicationService = null
		) : IProjection
	{
		public event EventHandler<EventArgs>? CommandProcessed;

		FileObjectStore _objectStore => (FileObjectStore)_lazyObjectStore.Value;

		/// <summary>
		/// Processing command is something that happens where action occured in order to legally persist the true fact about this action - set of events.
		/// Master Server is doing so to get the real truth about the result and officially persist events for good.
		/// Original client is doint so in order to start events antisipation (predict events), and apply events to the in-memory attached model.
		/// Antisipated events are marked as virtually-executed to avoid extra work when their true version will came back from master server.
		/// </summary>
		/// <param name="newCommand"></param>
		/// <returns></returns>
		/// <exception cref="Exception"></exception>
		public async Task ProcessCommandAsync(ISynqraCommand newCommand)
		{
			var commandHandlingContext = new CommandHandlerContext();
			if (newCommand is not Command cmd)
			{
				throw new Exception("Only Syncra.Command can be an implementation of ICommand, please derive from Syncra.Command");
			}
			await cmd.AcceptAsync(this, commandHandlingContext);
			var eventVisitorContext = new EventVisitorContext();
			foreach (var @event in commandHandlingContext.Events)
			{
				await @event.AcceptAsync(this, eventVisitorContext); // error handling - how to rollback state of entire model?
			}
			if (_eventStorage != null)
			{
				await _eventStorage.AppendBatchAsync(commandHandlingContext.Events); // store event in storage and trigger replication
			}
			CommandProcessed?.Invoke(this, EventArgs.Empty);
			_eventReplicationService?.Trigger(cmd, commandHandlingContext.Events);
		}

		public async Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx)
		{
			var created = new CommandCreatedEvent
			{
				EventId = GuidExtensions.CreateVersion7(),
				Data = cmd,
				CommandId = cmd.CommandId,
				ContainerId = cmd.ContainerId,
			};
			ctx.Events.Add(created);
		}

		public async Task AfterVisitAsync(Command cmd, CommandHandlerContext ctx)
		{
		}

		public Task BeforeVisitAsync(Event ev, EventVisitorContext ctx)
		{
			if (ev.EventId == default)
			{
				throw new Exception("Event id is not set");
			}
			if (ev is SingleObjectEvent sev)
			{
				if (sev.TargetTypeId == default)
				{
					throw new Exception("TargetTypeId is not set");
				}
				if (sev.TargetId == default)
				{
					throw new Exception("TargetId is not set");
				}
			}
			return Task.CompletedTask;
		}

		public async Task AfterVisitAsync(Event ev, EventVisitorContext ctx)
		{
		}

		public async Task VisitAsync(CreateObjectCommand cmd, CommandHandlerContext ctx)
		{
			var type = _typeMetadataProvider.GetTypeMetadata(cmd.TargetTypeId);
			if (type is null)
			{
				throw new Exception("Unknown TypeId");
			}

			if (cmd.CollectionId == default)
			{
				throw new ArgumentException("CollectionId is not specified", nameof(cmd));
			}
			if (cmd.CommandId == default)
			{
				throw new ArgumentException("CommandId is not specified", nameof(cmd));
			}
			if (cmd.ContainerId == default)
			{
				throw new ArgumentException("ContainerId is not specified", nameof(cmd));
			}
			if (cmd.TargetId == default)
			{
				throw new ArgumentException("TargetId is not specified", nameof(cmd));
			}

			if (cmd.TargetObject == null)
			{
				throw new ArgumentException("TargetObject is not set", nameof(cmd));
			}
			if (!type.Type.IsAssignableFrom(cmd.TargetObject.GetType()))
			{
				throw new ArgumentException($"TargetObject is of type {cmd.TargetObject.GetType()} but declared as {type.Type.Name}");
			}
			if (cmd.TargetId != _objectStore.GetId(cmd.TargetObject))
			{
				throw new ArgumentException($"command.TargetId {cmd.TargetId} is not equal to object id {_objectStore.GetId(cmd.TargetObject)}");
			}

			// ParentId is allowed for PostObject as speciaCase but is not allowed for ObjectCreated
			/*
			Id parentId = default;
			if (typeof(IHierarchy).IsAssignableFrom(type) || typeof(IMHierarchy).IsAssignableFrom(type)) // even though NH does not have field 'parentId' we can use same convention to allow specify first parent
			{
				var parentIdKey = nameof(IHierarchy.ParentId).ToCamel();
				if (data.TryGetValue(parentIdKey, out var parentIdValue))
				{
					parentId = parentIdValue is null ? default : Convert.ToString(parentIdValue).ToId();
					data.Remove(parentIdKey);
				}
			}
			*/

			var created = new ObjectCreatedEvent
			{
				ContainerId = cmd.ContainerId,
				EventId = GuidExtensions.CreateVersion7(),
				CollectionId = cmd.CollectionId,
				CommandId = cmd.CommandId,
				TargetTypeId = cmd.TargetTypeId,
				TargetId = cmd.TargetId,
				Data = cmd.Data,
				DataObject = cmd.TargetObject ?? throw new ArgumentException(nameof(cmd)), // or may be entire object
			};
			ctx.Events.Add(created);

			if (false && cmd.Data is IBindableModel bm)
			{

			}
			else
			{
				var pros = cmd.Data.GetType().GetProperties().Where(x => x.CanWrite && x.CanRead);
				foreach (var pro in pros)
				{
					var value = pro.GetValue(cmd.Data);
					// SynqraTypeExtensions
					if (Equals(value, pro.PropertyType.GetDefault()))
					{
						continue;
					}
					if (value != null)
					{
						ctx.Events.Add(new ObjectPropertyChangedEvent
						{
							ContainerId = cmd.ContainerId,
							CommandId = cmd.CommandId,
							CollectionId = cmd.CollectionId,
							EventId = GuidExtensions.CreateVersion7(),
							TargetTypeId = cmd.TargetTypeId,
							TargetId = cmd.TargetId,
							PropertyName = pro.Name,
							OldValue = null,
							NewValue = value,
						});
					}
				}
			}

			/*
			foreach (var kvp in data)
			{
				ctx.Events.Add(new ObjectPropertyChangedEvent
				{

				});
			}
			*/
			/*
			if (parentId != default)
			{
				ctx.Events.Add(new NodeMoved
				{
					Discriminator = cmd.Discriminator,
					UserId = cmd.UserId,
					TargetId = cmd.TargetId,
					NewValue = parentId,
				});
			}
			*/

		}

		public Task VisitAsync(DeleteObjectCommand cmd, CommandHandlerContext ctx)
		{
			throw new NotImplementedException();
		}

		public Task VisitAsync(ChangeObjectPropertyCommand cmd, CommandHandlerContext ctx)
		{
			var ev = new ObjectPropertyChangedEvent
			{
				ContainerId = cmd.ContainerId,
				EventId = GuidExtensions.CreateVersion7(),
				CollectionId = cmd.CollectionId,
				CommandId = cmd.CommandId,
				TargetTypeId = cmd.TargetTypeId,
				TargetId = cmd.TargetId,
				OldValue = cmd.OldValue,
				NewValue = cmd.NewValue,
				PropertyName = cmd.PropertyName,
			};
			ctx.Events.Add(ev);
			return Task.CompletedTask;
		}

		public async Task VisitAsync(ObjectCreatedEvent ev, EventVisitorContext ctx)
		{
			if (ev.DataObject == null)
			{
				throw new NotImplementedException();
			}
			await _appendStores.ItemAppendStorage.AppendAsync(new Item
			{
				ObjectId = ev.TargetId,
				CollectionId = ev.CollectionId,
				Blob = ev.DataObject,
			});
		}

		public async Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx)
		{
			var tm = _typeMetadataProvider.GetTypeMetadata(ev.TargetTypeId); // target type is collection root type
			var col = _objectStore.GetCollection(tm.Type, ev.CollectionId);

			var model = col[ev.TargetId];
			if (model is IBindableModel bm)
			{
				bm.Set(ev.PropertyName, ev.NewValue);
			}
			else if (model is not null)
			{
				// throw new Exception($"The type '{data.Model.GetType().Name}' is not IBindableModel. Please add 'partial' keyword for generator to work.");
				var pi = model.GetType().GetProperty(ev.PropertyName) ?? throw new Exception("Property not found");
				var value = ev.NewValue;
				if (ev.NewValue is IConvertible c)
				{
					value = c.ToType(pi.PropertyType, CultureInfo.InvariantCulture);
				}
				pi?.SetValue(model, Convert.ChangeType(value, pi.PropertyType));
			}
			else
			{
				throw new Exception($"Cannot change property of unknown object {ev.TargetId}");
			}
			await _appendStores.ItemAppendStorage.AppendAsync(new Item
			{
				ObjectId = ev.TargetId,
				CollectionId = ev.CollectionId,
				Blob = model,
			});
		}

		public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx)
		{
			throw new NotImplementedException();
		}

		public async Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx)
		{
			await _appendStores.ItemAppendStorage.AppendAsync(new Item
			{
				CollectionId = _objectStore.TypeMetadataProvider.GetTypeMetadata(typeof(Command)).GetCollectionId(""),
				ObjectId = ev.CommandId,
				Blob = ev.Data,
			});
			// await _appendStores.CommandAppendStorage.AppendAsync(ev.Data);
		}
	}

	private class AppendStores
	{
		public IAppendStorage<Event, Guid> EventAppendStorage { get; } // this always corresponds to current event store
		public IAppendStorage<Command, Guid> CommandAppendStorage { get; }
		public IAppendStorage<Item, (Guid, Guid)> ItemAppendStorage { get; }

		public AppendStores(IAppendStorage<Event, Guid> eventAppendStorage, IAppendStorage<Command, Guid> commandAppendStorage, IAppendStorage<Item, (Guid, Guid)> itemAppendStorage)
		{
			EventAppendStorage = eventAppendStorage;
			CommandAppendStorage = commandAppendStorage;
			ItemAppendStorage = itemAppendStorage;
		}
	}
}

[SynqraModel]
[Schema(1, "0")]
[Schema(3000.0, "1 ObjectId Guid Blob object")]
public sealed partial class Item
{
	public partial Guid ObjectId { get; set; }

	[JsonIgnore]
	public Guid CollectionId { get; set; }
	public partial object Blob { get; set; }
}
