namespace Synqra;

/// <summary>
/// Base for a reified relation between two points in the model graph. An edge rides the normal
/// object lifecycle — created via <see cref="CreateObjectCommand"/>/<see cref="ObjectCreatedEvent"/>
/// like any other object, with its own store-assigned identity (no separate "EdgeId" field, same
/// as a plain container object has none) — so it gets replication, concurrency and serialization
/// for free. The projection recognizes <see cref="Edge"/>-derived objects on create and maintains
/// an adjacency index (<see cref="IEdgeIndex"/>) plus structural dedup via <see cref="StructuralKey"/>.
/// <para>
/// Endpoints are stored flattened into scalar columns (the model-binding/SBX generators store
/// columns, not structs) and surfaced as <see cref="A"/>/<see cref="B"/> via read-only
/// expression-bodied accessors, which the generator does not treat as stored fields — same
/// convention the original <c>Wire</c> type used. See plans/edges.md.
/// </para>
/// </summary>
[SynqraModel]
[Schema(2026.410, "1 AObjectId Guid AComponentTypeId Guid AComponentId Guid BObjectId Guid BComponentTypeId Guid BComponentId Guid")]
public abstract partial class Edge : IIdentifiable<Guid>
{
	// Endpoint A (flattened — see remarks above).
	public partial Guid AObjectId { get; set; }
	public partial Guid AComponentTypeId { get; set; }
	public partial Guid AComponentId { get; set; }

	// Endpoint B.
	public partial Guid BObjectId { get; set; }
	public partial Guid BComponentTypeId { get; set; }
	public partial Guid BComponentId { get; set; }

	/// <summary>Store-assigned identity, like any other plain object.</summary>
	Guid IIdentifiable<Guid>.Id => ((IBindableModel)this).Store?.GetId(this) ?? default;

	/// <summary>Endpoint A as a value tuple.</summary>
	public Ref A => new(AObjectId, AComponentTypeId, AComponentId);

	/// <summary>Endpoint B as a value tuple.</summary>
	public Ref B => new(BObjectId, BComponentTypeId, BComponentId);

	/// <summary>Identity-independent dedup/upsert key. Directionality decides how A/B fold into it.</summary>
	public abstract EdgeKey StructuralKey { get; }
}
