namespace Synqra;

/// <summary>
/// Object and facet lookups a store answers from its own indexes, so a host never enumerates every collection to find
/// one thing (docs/model.md §2). Mongo: <c>_id</c>, <c>(_sid, _t, _id)</c> and <c>(_sid, _t, _eid)</c>; in memory: maps.
/// </summary>
public interface IComponentIndex
{
	/// <summary>The facet with this id and the object it belongs to, or null when the stream has no such facet. A root is an object: see <see cref="IObjectStore.ResolveObject"/>.</summary>
	ComponentHit? FindComponent(Guid componentId);

	/// <summary>The stream's objects matching <paramref name="query"/>, in id order, read lazily.</summary>
	IEnumerable<object> FindObjects(ObjectQuery query);

	/// <summary>
	/// The object each id belongs to, keyed by that id: an object owns itself, a facet or a link the object that holds it.
	/// The owners come tracked with their facets; an id the stream lacks is left out. Answers many ids at once, so a caller
	/// resolving a batch pays for the batch, not for each id.
	/// </summary>
	IReadOnlyDictionary<Guid, object> FindOwners(IEnumerable<Guid> ids);
}
