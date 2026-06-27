namespace Synqra;

/// <summary>
/// Declares a navigation property over the <b>target</b> side of a link: "links originating from
/// me — give me what they point at" (e.g. <c>Children</c>, <c>Blocks</c>). The declaring node is the
/// link's source.
/// <para>
/// When the property element type is itself a <see cref="Link"/> (link-typed navigation, used for
/// links that carry payload), the link type is taken from the element type and the
/// <see cref="LinkType"/> argument is optional. When the element type is a node, the link type must
/// be supplied and must be a <i>primitive</i> link (no payload of its own).
/// </para>
/// <para>
/// Single-valued (non-collection) node-typed navigation is read-only by default — declare the
/// property with a setter (<c>{ get; set; }</c>, not <c>{ get; }</c>) to opt into one. The generated
/// setter replaces whatever single link already occupied that role (if any) with a new one to the
/// assigned value, or removes it on <c>null</c>. Not generated for collection-typed properties.
/// </para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple = false)]
public sealed class ToAttribute : System.Attribute
{
	public ToAttribute() { }
	public ToAttribute(System.Type linkType) { LinkType = linkType; }

	/// <summary>Concrete link type. Optional for link-typed navigation; required for node-typed.</summary>
	public System.Type? LinkType { get; }
}

/// <summary>
/// Declares a navigation property over the <b>source</b> side of a link: "links pointing at me —
/// give me where they come from" (e.g. <c>Parent</c>, <c>BlockedBy</c>). The declaring node is the
/// link's target. See <see cref="ToAttribute"/> for the node-typed vs. link-typed rules and the
/// opt-in setter convention.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple = false)]
public sealed class FromAttribute : System.Attribute
{
	public FromAttribute() { }
	public FromAttribute(System.Type linkType) { LinkType = linkType; }

	/// <summary>Concrete link type. Optional for link-typed navigation; required for node-typed.</summary>
	public System.Type? LinkType { get; }
}

/// <summary>
/// Declares a navigation property over an <see cref="UndirectedLink{TSource, TTarget}"/>: "links
/// incident to me — give me the other end, whichever side I am on". See <see cref="ToAttribute"/>
/// for the node-typed vs. link-typed rules and the opt-in setter convention.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple = false)]
public sealed class RelatedAttribute : System.Attribute
{
	public RelatedAttribute() { }
	public RelatedAttribute(System.Type linkType) { LinkType = linkType; }

	/// <summary>Concrete link type. Optional for link-typed navigation; required for node-typed.</summary>
	public System.Type? LinkType { get; }
}
