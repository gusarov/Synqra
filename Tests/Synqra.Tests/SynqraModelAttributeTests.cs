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

		await Assert.That(metadata.TypeId).IsEqualTo(new Guid("C0DE0000-0000-8000-9000-000000000001"));
	}

	[SynqraModel("C0DE0000-0000-8000-9000-000000000001")]
	private sealed class ExplicitTypeIdModel
	{
	}
}
