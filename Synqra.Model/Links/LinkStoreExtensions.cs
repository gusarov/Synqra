namespace Synqra;

/// <summary>
/// The component commands a link mutation submits. A link is owned by its source, so it is added to and
/// removed from the source entity; an undirected link is owned by its lower endpoint id, so concurrent
/// creates from either end converge on one owner (docs/model.md §4).
/// </summary>
public static class LinkStoreExtensions
{
	/// <summary>Adds <paramref name="link"/> to the store as a component of its owner, minting its id when it has none.</summary>
	public static void SubmitAddLink(this IObjectStore store, Link link)
	{
		if (link is null)
		{
			throw new System.ArgumentNullException(nameof(link));
		}
		if (link.IsUndirected && link.SourceType == link.TargetType && link.TargetId.CompareTo(link.SourceId) < 0)
		{
			(link.SourceId, link.TargetId) = (link.TargetId, link.SourceId);
		}
		var owner = store.ResolveObject(link.SourceId)
			?? throw new System.InvalidOperationException($"A '{link.GetType().Name}' link cannot be added: its source {link.SourceId} is not in the store.");
		if (link.LinkId == default)
		{
			((IBindableComponent)link).SetComponentId(store.IdProvider.CreateLinkId());
		}
		var ownerMetadata = store.TypeMetadataProvider.GetTypeMetadata(owner.GetType());
		store.SubmitCommand(
			new AddComponentCommand
			{
				CollectionId = ownerMetadata.CollectionId,
				TargetId = link.SourceId,
				TargetTypeId = ownerMetadata.TypeId,
				ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(link.GetType()).TypeId,
				ComponentId = link.LinkId,
				Data = link,
			},
			new CommandSubmissionOptions { ExpectedLastEventId = store.GetLastEventId(link.SourceId) });
	}

	/// <summary>Removes a stored <paramref name="link"/> from its owner; neither endpoint is touched.</summary>
	public static void SubmitRemoveLink(this IObjectStore store, Link link)
	{
		if (link is null)
		{
			throw new System.ArgumentNullException(nameof(link));
		}
		var owner = store.ResolveObject(link.SourceId)
			?? throw new System.InvalidOperationException($"A '{link.GetType().Name}' link cannot be removed: its source {link.SourceId} is not in the store.");
		var ownerMetadata = store.TypeMetadataProvider.GetTypeMetadata(owner.GetType());
		store.SubmitCommand(
			new DeleteComponentCommand
			{
				CollectionId = ownerMetadata.CollectionId,
				TargetId = link.SourceId,
				TargetTypeId = ownerMetadata.TypeId,
				ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(link.GetType()).TypeId,
				ComponentId = link.LinkId,
			},
			new CommandSubmissionOptions { ExpectedLastEventId = store.GetLastEventId(link.SourceId) });
	}
}
