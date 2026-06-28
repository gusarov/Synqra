using System.Collections.Generic;

namespace Synqra;

/// <summary>
/// Non-generic base for a reified relation between two model objects. Unlike a plain object, a
/// link does NOT ride <see cref="CreateObjectCommand"/>/<see cref="ObjectCreatedEvent"/> — it has
/// its own dedicated <see cref="AddLinkCommand"/>/<see cref="LinkAddedEvent"/> (and
/// <see cref="RemoveLinkCommand"/>/<see cref="LinkRemovedEvent"/>), the same way a component has
/// <c>AddComponentCommand</c>/<c>ComponentAddedEvent</c> instead of riding the generic object
/// lifecycle: a link is an attached fact between two objects, not a top-level object in its own
/// right. Carrying both endpoints in one atomic event also means there is no reconstruction window
/// on replay — unlike the generic object path, where a property arrives in a separate event after
/// creation, here <see cref="SourceId"/>/<see cref="TargetId"/> are always already set the moment
/// the event is materialized.
/// <para>
/// Endpoint identities are stored as two scalar id columns (<see cref="SourceId"/> /
/// <see cref="TargetId"/>) which the model/SBX generators persist. Consumers do not work with
/// these directly: the typed <see cref="Link{TSource, TTarget}.Source"/> /
/// <see cref="Link{TSource, TTarget}.Target"/> accessors and the <c>[To]</c>/<c>[From]</c>/<c>[Related]</c>
/// navigation properties on their own models give a fully typed surface. See plans/links.md.
/// </para>
/// </summary>
[SynqraModel]
[Schema(2026.500, "1 LinkId Guid SourceId Guid TargetId Guid")]
public abstract partial class Link : IIdentifiable<Guid>
{
	/// <summary>Own identity — assigned by the caller (or the store, if left default) when the link is created, not by store attachment.</summary>
	public partial Guid LinkId { get; set; }

	/// <summary>Identity of the object at the link's source/from end.</summary>
	public partial Guid SourceId { get; set; }

	/// <summary>Identity of the object at the link's target/to end.</summary>
	public partial Guid TargetId { get; set; }

	Guid IIdentifiable<Guid>.Id => LinkId;

	/// <summary>Declared type of the source endpoint.</summary>
	public abstract System.Type SourceType { get; }

	/// <summary>Declared type of the target endpoint.</summary>
	public abstract System.Type TargetType { get; }

	/// <summary>
	/// Identity-independent dedup/upsert key. <see cref="DirectedLink{TSource, TTarget}"/> folds the
	/// endpoints in order (A→B ≠ B→A); <see cref="UndirectedLink{TSource, TTarget}"/> folds them
	/// symmetrically ({A,B} == {B,A}).
	/// </summary>
	public abstract LinkKey StructuralKey { get; }

	/// <summary>Resolve an endpoint id to its model object through the attached store, or null.</summary>
	protected object? ResolveEndpoint(Guid id)
	{
		if (id == default)
		{
			return null;
		}
		return ((IBindableModel)this).Store?.ResolveObject(id);
	}

	/// <summary>
	/// Property names every concrete <see cref="Link"/> always carries that are already explicit,
	/// top-level fields on <see cref="AddLinkCommand"/>/<see cref="LinkAddedEvent"/> — pass to
	/// <see cref="ObjectData.From(object, ISet{string}?)"/> when normalizing a link instance
	/// into <see cref="AddLinkCommand.Data"/> so the bag only ever carries a concrete subtype's own
	/// extra properties (e.g. <c>WeightedLink.Order</c>), not a second copy of these three.
	/// </summary>
	public static readonly ISet<string> WellKnownDataFields =
		new HashSet<string> { nameof(LinkId), nameof(SourceId), nameof(TargetId) };
}
