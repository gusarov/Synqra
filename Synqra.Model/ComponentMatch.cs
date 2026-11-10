namespace Synqra;

/// <summary>
/// An equality predicate on a component property: the object carries a facet of <see cref="Component"/> (or a registered
/// subtype) whose <see cref="Property"/> equals <see cref="Value"/>. A root is its own first component, so a match on a root
/// type reads the object's own property. A type or property the model does not have is refused, never matched to nothing.
/// </summary>
public sealed record ComponentMatch(Type Component, string Property, object? Value);
