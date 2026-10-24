using Synqra;

namespace Contoso.Model;

/// <summary>
/// The one item a brand-new Contoso stream starts with. Its creator seeds it: the browser for a stream it made, the master for a
/// stream the server made (<see cref="IContosoServerStreams"/>). A master never claims a browser's stream: that one reaches it
/// empty at the browser's first connect, before the browser has sent its own seed.
/// </summary>
public sealed class ContosoWelcomeSeeder(IContosoServerStreams? serverStreams = null) : IStreamSeeder
{
	public const string ItemName = "Welcome";

	public async Task SeedAsync(StreamSeedContext context, IObjectStore store)
	{
		if (context.Origin == StreamOrigin.CreatedHere || serverStreams?.Contains(context.StreamId) == true)
		{
			var item = store.TypeMetadataProvider.GetTypeMetadata(typeof(ContosoItem));
			var id = store.IdProvider.CreateComponentId();
			await store.SubmitCommandAsync(new AddComponentCommand
			{
				TargetTypeId = item.TypeId,
				CollectionId = item.CollectionId,
				TargetId = id,
				ComponentTypeId = item.TypeId,
				ComponentId = id,
				Data = new ContosoItem { ItemName = ItemName },
			});
		}
	}
}
