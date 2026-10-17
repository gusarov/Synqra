namespace Synqra;

/// <summary>
/// What <see cref="IComponentIndex.FindObjects"/> narrows by, combined with AND. A type stands for itself and every
/// registered subtype. <see cref="Has"/>: the object carries a facet of that type. <see cref="Ids"/>: one of these ids.
/// <see cref="After"/>: ids strictly after this cursor. <see cref="Text"/>: every one of its <see cref="Words"/> appears in
/// the object's text or one of its facets' (string values and type names), whole and case-insensitive.
/// </summary>
public sealed record ObjectQuery(IReadOnlyCollection<Type> Types)
{
	public Type? Has { get; init; }

	public IReadOnlyCollection<Guid>? Ids { get; init; }

	public Guid After { get; init; }

	public string? Text { get; init; }

	/// <summary>The whitespace-separated words of <see cref="Text"/>, quotes dropped, once each; a word with no letter or digit is ignored.</summary>
	public IReadOnlyList<string> Words => (Text ?? "")
		.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
		.Select(w => w.Replace("\"", ""))
		.Where(w => w.Any(char.IsLetterOrDigit))
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.ToList();
}
