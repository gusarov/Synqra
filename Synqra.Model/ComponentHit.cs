namespace Synqra;

/// <summary>A facet found by its id, with the object it belongs to.</summary>
public sealed record ComponentHit(object Container, IComponent Component);
