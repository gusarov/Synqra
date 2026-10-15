using System.Text.Json;
using Synqra.Tests.SampleModels;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// The discriminator is the model's simple name and the registry is process-wide (JSON, Mongo and SBX all
/// read it), so two models with the same name cannot coexist: the second is refused when it registers, not
/// when the first document fails to serialize.
/// </summary>
public class JsonDiscriminatorTests
{
	[Test]
	public async Task Should_refuse_a_second_model_with_the_same_name_at_registration()
	{
		SynqraJsonTypeInfoResolver.RegisterGeneratedModel(typeof(SampleModels.TwinA.Twin));
		SynqraJsonTypeInfoResolver.RegisterGeneratedModel(typeof(SampleModels.TwinA.Twin)); // the same model again is fine

		var refused = Assert.Throws<SynqraJsonException>(() => SynqraJsonTypeInfoResolver.RegisterGeneratedModel(typeof(SampleModels.TwinB.Twin)));

		await Assert.That(refused.Message).Contains("TwinA.Twin");
		await Assert.That(refused.Message).Contains("TwinB.Twin");
		// The first owner keeps working, and the refused twin left nothing behind.
		var options = new JsonSerializerOptions(SampleJsonSerializerContext.DefaultOptions); // fresh: a cached options instance finalized its derived types before this model existed
		var json = JsonSerializer.Serialize<object>(new SampleModels.TwinA.Twin { Name = "a" }, options);
		await Assert.That(json.Replace(" ", "")).Contains("\"_t\":\"Twin\"");
		await Assert.That(SynqraJsonTypeInfoResolver.GetByDiscriminator("Twin")).IsEqualTo(typeof(SampleModels.TwinA.Twin));
	}
}
