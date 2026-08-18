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

		await Assert.That(metadata.TypeId).IsEqualTo(new Guid("C0DE0000-0000-8000-9044-000000000000"));
	}

	[Test]
	public async Task Should_resolve_legacy_type_id_alias_for_registered_model()
	{
		ServiceCollection.AddTypeMetadataProvider(typeof(ExplicitTypeIdModel));

		var provider = ServiceProvider.GetRequiredService<ITypeMetadataProvider>();
		// Resolving by the FORMER id still returns the type, now carrying its CURRENT id —
		// this is how a type's id can be changed without orphaning already-persisted data.
		var metadata = provider.GetTypeMetadata(new Guid("C0DE0000-0000-8000-9043-000000000000"));

		await Assert.That(metadata.Type).IsEqualTo(typeof(ExplicitTypeIdModel));
		await Assert.That(metadata.TypeId).IsEqualTo(new Guid("C0DE0000-0000-8000-9044-000000000000"));
	}

	// 9044: mode 9 (staging registry, pinned), family 0 (default/unqualified — a plain domain model,
	// neither a command nor an event), local code 44. The node is all-zero because the id names a
	// *type*, not an instance.
	[SynqraModel("C0DE0000-0000-8000-9044-000000000000")]
	[SynqraLegacyTypeId("C0DE0000-0000-8000-9043-000000000000", "2026-07-19", "test: former id (9043) must still resolve after change to 9044")]
	private sealed class ExplicitTypeIdModel
	{
	}
}
