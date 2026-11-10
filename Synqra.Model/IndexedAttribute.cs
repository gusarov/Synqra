namespace Synqra;

/// <summary>Marks a component property the projection indexes, so <see cref="ObjectQuery.Where"/> on it is an index lookup, never a scan (docs/model.md §2).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IndexedAttribute : Attribute
{
}
