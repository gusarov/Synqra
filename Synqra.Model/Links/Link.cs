using System.Text.Json.Serialization;
namespace Synqra;

/// <summary>
/// Non-generic base for a reified relation between two model objects. A link IS a component
/// (docs/model.md §4): it is owned by its source entity and points at its target, rides
/// <see cref="AddComponentCommand"/>/<see cref="ChangeComponentPropertyCommand"/>/<see cref="DeleteComponentCommand"/>
/// like any other component, and keeps its id across edits — retargeting is an in-place change of
/// <see cref="TargetId"/>. The store additionally indexes it by both endpoints and by its
/// <see cref="StructuralKey"/> (<see cref="ILinkIndex"/>).
/// <para>
/// Endpoint identities are two scalar id columns. <see cref="SourceId"/> is the owning entity and is
/// stamped by the store from the component's owner; <see cref="TargetId"/> is the one editable end.
/// Consumers normally use the typed <see cref="Link{TSource, TTarget}.Source"/> /
/// <see cref="Link{TSource, TTarget}.Target"/> accessors and the <c>[To]</c>/<c>[From]</c>/<c>[Related]</c>
/// navigation properties on their own models.
/// </para>
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9300-000000000000")]
[Schema(2026.500, "1 LinkId Guid SourceId Guid TargetId Guid")]
[Schema(2026.794, "1 SourceId Guid TargetId Guid")]
public abstract partial class Link : IComponent
{
	/// <summary>The link's own identity — its component id, which stays the same when the link is retargeted.</summary>
	[JsonIgnore]
	public System.Guid LinkId => __id;

	/// <summary>Identity of the object at the link's source/from end — the entity that owns it.</summary>
	public partial System.Guid SourceId { get; set; }

	/// <summary>Identity of the object at the link's target/to end. Setting it on a stored link retargets that link in place.</summary>
	public partial System.Guid TargetId { get; set; }

	/// <summary>Declared type of the source endpoint.</summary>
	internal abstract System.Type SourceType { get; } // a System.Type is not data: never on the wire

	/// <summary>Declared type of the target endpoint.</summary>
	internal abstract System.Type TargetType { get; }

	/// <summary>
	/// Identity-independent dedup/upsert key. <see cref="DirectedLink{TSource, TTarget}"/> folds the
	/// endpoints in order (A→B ≠ B→A); <see cref="UndirectedLink{TSource, TTarget}"/> folds them
	/// symmetrically ({A,B} == {B,A}).
	/// </summary>
	[JsonIgnore] // derived, and it carries a System.Type: not data, never on the wire
	internal abstract LinkKey StructuralKey { get; } // derived, carries a System.Type: an index key, not data, never on the wire

	/// <summary>Whether the two ends are interchangeable, so the store owns the link on the lower id (docs/model.md §4).</summary>
	internal abstract bool IsUndirected { get; }

	/// <summary>Resolve an endpoint id to its model object through the attached store, or null.</summary>
	protected object? ResolveEndpoint(System.Guid id)
	{
		object? endpoint = null;
		if (id != default)
		{
			endpoint = ((IBindableModel)this).Store?.ResolveObject(id);
		}
		return endpoint;
	}
}
