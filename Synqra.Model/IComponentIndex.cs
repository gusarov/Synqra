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
}
