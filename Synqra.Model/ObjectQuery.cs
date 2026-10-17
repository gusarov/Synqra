namespace Synqra;

/// <summary>
/// What <see cref="IComponentIndex.FindObjects"/> narrows by, combined with AND. A type stands for itself and every
/// registered subtype. <see cref="Has"/>: the object carries a facet of that type. <see cref="Ids"/>: one of these ids.
/// <see cref="After"/>: ids strictly after this cursor.
/// </summary>
public sealed record ObjectQuery(IReadOnlyCollection<Type> Types)
{
	public Type? Has { get; init; }

	public IReadOnlyCollection<Guid>? Ids { get; init; }

	public Guid After { get; init; }
}
