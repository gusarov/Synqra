using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.TestHelpers;

namespace Synqra.Tests;

public class SynqraModelAttributeTests : BaseTest
{
	[Test]
	public async Task Should_use_explicit_string_type_id_for_registered_model()
	{
		ServiceCollection.AddTypeMetadataProvider(typeof(ExplicitTypeIdModel));

		var provider = ServiceProvider.GetRequiredService<ITypeMetadataProvider>();
		var metadata = provider.GetTypeMetadata(typeof(ExplicitTypeIdModel));

		await Assert.That(metadata.TypeId).IsEqualTo(new Guid("36c3d5f8-454c-55ae-9922-c8052a7617cc"));
	}

	[SynqraModel("36c3d5f8-454c-55ae-9922-c8052a7617cc")]
	private sealed class ExplicitTypeIdModel
	{
	}
}
