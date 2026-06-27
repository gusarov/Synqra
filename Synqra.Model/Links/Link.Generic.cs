namespace Synqra;

using System.Text.Json.Serialization;

/// <summary>
/// Typed reified relation between two model objects. The two type parameters are the consumer's
/// own model types — a consumer declares <c>class HierarchyLink : DirectedLink&lt;TodoNode, TodoNode&gt;</c>
/// and never sees a raw <see cref="System.Guid"/> or any framework endpoint-reference type.
/// <para>
/// Endpoint identities are stored as scalar columns on the non-generic <see cref="Link"/> base
/// (so the SBX/model generators persist them) and surfaced here as the typed, store-resolved
/// <see cref="Source"/> / <see cref="Target"/> navigation properties. These accessors are not
/// persisted — the generator only stamps the scalar id columns.
/// </para>
/// </summary>
public abstract class Link<TSource, TTarget> : Link
	where TSource : class
	where TTarget : class
{
	public override System.Type SourceType => typeof(TSource);
	public override System.Type TargetType => typeof(TTarget);

	/// <summary>The object at the link's source end, resolved through the store. Setting it records the source identity.</summary>
	[JsonIgnore]
	public TSource? Source
	{
		get => (TSource?)ResolveEndpoint(SourceId);
		set => SourceId = IdOf(value);
	}

	/// <summary>The object at the link's target end, resolved through the store. Setting it records the target identity.</summary>
	[JsonIgnore]
	public TTarget? Target
	{
		get => (TTarget?)ResolveEndpoint(TargetId);
		set => TargetId = IdOf(value);
	}

	static System.Guid IdOf(object? node)
	{
		if (node is null)
		{
			return default;
		}
		if (node is IBindableModel { Store: { } store })
		{
			return store.GetId(node);
		}
		throw new System.InvalidOperationException(
			"A link endpoint must be an object that is already attached to the store before it can be linked.");
	}
}
