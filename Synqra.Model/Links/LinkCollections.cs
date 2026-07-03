using System.Collections;
using System.Collections.Generic;

namespace Synqra;

/// <summary>
/// Shared base for the live, store-backed collections returned by generated link-navigation
/// properties. It never holds a snapshot: every enumeration re-queries the store's
/// <see cref="ILinkIndex"/> for links incident to the owning node at the relevant
/// <see cref="LinkEnd"/>, so the view always reflects current state. <see cref="Add"/> submits
/// <see cref="AddLinkCommand"/> — there is no separate <c>Link</c> call. <see cref="Remove"/>/
/// <see cref="Clear"/> submit <see cref="RemoveLinkCommand"/>.
/// </summary>
public abstract class LinkNavCollectionBase<TItem, TLink> : ICollection<TItem>, IReadOnlyList<TItem>
	where TItem : class
	where TLink : Link
{
	protected readonly IBindableModel _owner;
	protected readonly LinkEnd _selfEnd;

	protected LinkNavCollectionBase(IBindableModel owner, LinkEnd selfEnd)
	{
		_owner = owner ?? throw new System.ArgumentNullException(nameof(owner));
		if (selfEnd == LinkEnd.None)
		{
			throw new System.ArgumentException("LinkEnd.None is not a valid link end — every navigation collection must specify Source, Target, or Either.", nameof(selfEnd));
		}
		_selfEnd = selfEnd;
	}

	protected IObjectStore Store => _owner.Store
		?? throw new System.InvalidOperationException("The owning model is not attached to a store, so its links cannot be navigated or modified.");

	protected System.Guid OwnerId => Store.GetId(_owner);

	ILinkIndex? Index => Store as ILinkIndex;

	IEnumerable<TLink> IncidentLinks()
	{
		var index = Index;
		if (index is null)
		{
			yield break;
		}
		var ownerId = OwnerId;
		foreach (var link in index.LinksAt(ownerId, _selfEnd, typeof(TLink)))
		{
			yield return (TLink)link;
		}
	}

	protected System.Guid OtherEndId(TLink link) => _selfEnd switch
	{
		LinkEnd.Source => link.TargetId,
		LinkEnd.Target => link.SourceId,
		_ => link.SourceId == OwnerId ? link.TargetId : link.SourceId,
	};

	protected abstract TItem Project(TLink link);

	public IEnumerator<TItem> GetEnumerator()
	{
		foreach (var link in IncidentLinks())
		{
			yield return Project(link);
		}
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	public int Count
	{
		get
		{
			var n = 0;
			foreach (var _ in IncidentLinks())
			{
				n++;
			}
			return n;
		}
	}

	public TItem this[int index]
	{
		get
		{
			if (index < 0)
			{
				throw new System.ArgumentOutOfRangeException(nameof(index));
			}
			var i = 0;
			foreach (var item in this)
			{
				if (i++ == index)
				{
					return item;
				}
			}
			throw new System.ArgumentOutOfRangeException(nameof(index));
		}
	}

	public bool IsReadOnly => false;

	public abstract void Add(TItem item);

	/// <summary>The single item, or null — used by generated single-valued navigation (e.g. <c>Parent</c>).</summary>
	public TItem? SingleOrDefault()
	{
		foreach (var item in this)
		{
			return item;
		}
		return null;
	}

	/// <summary>
	/// Used by the generator's opt-in setter for single-valued navigation (see <see cref="ToAttribute"/>):
	/// removes whatever link already occupies this role, then adds a new one to <paramref name="item"/>
	/// unless it is <c>null</c>.
	/// </summary>
	public void SetSingle(TItem? item)
	{
		foreach (var link in IncidentLinksSnapshot())
		{
			SubmitRemoveLink(link);
		}
		if (item is not null)
		{
			Add(item);
		}
	}

	public bool Contains(TItem item)
	{
		foreach (var existing in this)
		{
			if (ReferenceEquals(existing, item) || Equals(existing, item))
			{
				return true;
			}
		}
		return false;
	}

	public void CopyTo(TItem[] array, int arrayIndex)
	{
		foreach (var item in this)
		{
			array[arrayIndex++] = item;
		}
	}

	public bool Remove(TItem item)
	{
		foreach (var link in IncidentLinksSnapshot())
		{
			var projected = Project(link);
			if (ReferenceEquals(projected, item) || Equals(projected, item))
			{
				SubmitRemoveLink(link);
				return true;
			}
		}
		return false;
	}

	public void Clear()
	{
		foreach (var link in IncidentLinksSnapshot())
		{
			SubmitRemoveLink(link);
		}
	}

	// Snapshot before mutating — removal updates the index mid-enumeration otherwise.
	TLink[] IncidentLinksSnapshot()
	{
		var list = new List<TLink>();
		foreach (var link in IncidentLinks())
		{
			list.Add(link);
		}
		return list.ToArray();
	}

	/// <summary>Submit a freshly-built link, skipping creation when an equivalent link already exists (idempotent link).</summary>
	protected void SubmitNewLink(TLink link)
	{
		if (Index is { } index && index.TryGetByKey(link.StructuralKey, out _))
		{
			return;
		}
		var store = Store;
		if (link.LinkId == default)
		{
			link.LinkId = GuidExtensions.CreateVersion7();
		}
		var task = store.SubmitCommandAsync(new AddLinkCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			LinkTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TLink)).TypeId,
			LinkId = link.LinkId,
			SourceId = link.SourceId,
			TargetId = link.TargetId,
			Data = link,
		});
		RunSync(task);
	}

	void SubmitRemoveLink(TLink link)
	{
		var task = Store.SubmitCommandAsync(new RemoveLinkCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			LinkId = link.LinkId,
		});
		RunSync(task);
	}

	// Mirrors InMemoryStoreCollection<T>.Add's WASM handling (Synqra.Projection.InMemory's
	// AsyncInvoker isn't visible from here — this assembly's own copy of the same one-liner).
	static async void FireAndForget(System.Threading.Tasks.Task task)
	{
		try
		{
			await task;
		}
		catch (System.Exception ex)
		{
			System.Console.Error.WriteLine($"LinkNavCollectionBase: {ex}");
		}
	}

	static void RunSync(System.Threading.Tasks.Task task)
	{
		if (System.OperatingSystem.IsBrowser())
		{
			FireAndForget(task);
		}
		else
		{
			task.GetAwaiter().GetResult();
		}
	}
}

/// <summary>
/// Link-typed navigation: the consumer's property element type is the link itself, so payload is
/// visible and settable (<c>new HierarchyLink { Target = child, Order = 1 }</c>). Used for links
/// that carry data. <see cref="Add"/> stamps the owner's end and submits the link.
/// </summary>
public sealed class LinkEndCollection<TLink> : LinkNavCollectionBase<TLink, TLink>
	where TLink : Link
{
	public LinkEndCollection(IBindableModel owner, LinkEnd selfEnd) : base(owner, selfEnd) { }

	protected override TLink Project(TLink link) => link;

	public override void Add(TLink link)
	{
		if (link is null)
		{
			throw new System.ArgumentNullException(nameof(link));
		}
		var ownerId = OwnerId;
		switch (_selfEnd)
		{
			case LinkEnd.Source:
				link.SourceId = ownerId;
				break;
			case LinkEnd.Target:
				link.TargetId = ownerId;
				break;
			default:
				if (link.SourceId == default)
				{
					link.SourceId = ownerId;
				}
				else
				{
					link.TargetId = ownerId;
				}
				break;
		}
		SubmitNewLink(link);
	}
}

/// <summary>
/// Node-typed navigation: the property element type is a node, and the link that connects them is
/// an implementation detail the consumer never sees. Only valid for <i>primitive</i> links (no
/// payload). <see cref="Add"/> creates the connecting link; enumeration resolves the opposite end
/// back to the node type.
/// </summary>
public sealed class NodeLinkCollection<TNode, TLink> : LinkNavCollectionBase<TNode, TLink>
	where TNode : class
	where TLink : Link, new()
{
	public NodeLinkCollection(IBindableModel owner, LinkEnd selfEnd) : base(owner, selfEnd) { }

	protected override TNode Project(TLink link) => (TNode)Store.ResolveObject(OtherEndId(link))!;

	public override void Add(TNode node)
	{
		if (node is null)
		{
			throw new System.ArgumentNullException(nameof(node));
		}
		var ownerId = OwnerId;
		var otherId = Store.GetId(node);
		var link = new TLink();
		switch (_selfEnd)
		{
			case LinkEnd.Target:
				link.SourceId = otherId;
				link.TargetId = ownerId;
				break;
			default:
				link.SourceId = ownerId;
				link.TargetId = otherId;
				break;
		}
		SubmitNewLink(link);
	}
}
