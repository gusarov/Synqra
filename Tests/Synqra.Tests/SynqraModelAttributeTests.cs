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

		await Assert.That(metadata.TypeId).IsEqualTo(new Guid("C0DE0000-0000-8000-9041-000000000000"));
	}

	[Test]
	public async Task Should_resolve_legacy_type_id_alias_for_registered_model()
	{
		ServiceCollection.AddTypeMetadataProvider(typeof(ExplicitTypeIdModel));

		var provider = ServiceProvider.GetRequiredService<ITypeMetadataProvider>();
		// Resolving by the FORMER id still returns the type, now carrying its CURRENT id —
		// this is how a type's id can be changed without orphaning already-persisted data.
		var metadata = provider.GetTypeMetadata(new Guid("C0DE0000-0000-8000-9040-000000000000"));

		await Assert.That(metadata.Type).IsEqualTo(typeof(ExplicitTypeIdModel));
		await Assert.That(metadata.TypeId).IsEqualTo(new Guid("C0DE0000-0000-8000-9041-000000000000"));
	}

	[SynqraModel("C0DE0000-0000-8000-9041-000000000000")] // 9041 where 9 means test, 41 means exact this test model class (unique per class and above any potential 640 production type codes from 8 space)
	[SynqraLegacyTypeId("C0DE0000-0000-8000-9040-000000000000", "2026-07-19", "test: former id (9040) must still resolve after change to 9041")] // a former id (e.g. 9040 before the change to 9041) — still resolves
	private sealed class ExplicitTypeIdModel
	{
	}
}
