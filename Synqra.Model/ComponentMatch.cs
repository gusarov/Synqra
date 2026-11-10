using System.Diagnostics.CodeAnalysis;

namespace Synqra;

/// <summary>
/// An equality predicate on a component property: the object carries a facet of <see cref="Component"/> (or a registered
/// subtype) whose <see cref="Property"/> equals <see cref="Value"/>. A root is its own first component, so a match on a root
/// type reads the object's own property. A type or property the model does not have is refused, never matched to nothing.
/// The component type keeps its public properties through trimming, so a match resolves under AOT as it does under the JIT.
/// </summary>
public sealed record ComponentMatch([property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type Component, string Property, object? Value);
