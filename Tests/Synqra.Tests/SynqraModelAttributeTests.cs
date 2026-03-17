using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.TestHelpers;

namespace Synqra.Tests;

public class SynqraModelAttributeTests : BaseTest
{
	[Test]
	public async Task Should_use_explicit_string_type_id_for_registered_model()
	{
		ServiceCollection.AddTypeMetadataProvider(typeof(LegacyTypeIdModel));

		var provider = ServiceProvider.GetRequiredService<ITypeMetadataProvider>();
		var metadata = provider.GetTypeMetadata(typeof(LegacyTypeIdModel));

		await Assert.That(metadata.TypeId).IsEqualTo(new Guid("36c3d5f8-454c-55ae-9922-c8052a7617cc"));
	}

	[Test]
	public async Task Should_resolve_legacy_type_id_alias_for_registered_model()
	{
		ServiceCollection.AddTypeMetadataProvider(typeof(LegacyTypeIdModel));

		var provider = ServiceProvider.GetRequiredService<ITypeMetadataProvider>();
		var metadata = provider.GetTypeMetadata(new Guid("f0225bde-4902-5166-8d0d-0ad5361a7037"));

		await Assert.That(metadata.Type).IsEqualTo(typeof(LegacyTypeIdModel));
		await Assert.That(metadata.TypeId).IsEqualTo(new Guid("36c3d5f8-454c-55ae-9922-c8052a7617cc"));
	}

	[SynqraModel("36c3d5f8-454c-55ae-9922-c8052a7617cc")]
	[SynqraLegacyTypeId("f0225bde-4902-5166-8d0d-0ad5361a7037")]
	private sealed class LegacyTypeIdModel
	{
	}
}
