using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra.Tests.GeneratorTests;

public class ModelTests
{
	private class SamplePrivateModel
	{
	}

	[SynqraModel(typeof(SamplePrivateModel))]
	private partial class SampleContext : StoreContext
	{
		public SampleContext(JsonSerializerContext jsonSerializerContext, IStorage storage) : base(jsonSerializerContext, storage)
		{
		}
	}

	[Test]
	public async Task Should_01_allow_set_model_properties()
	{
		var model = new SamplePrivateModel();
		await Assert.That(model).IsNotNull();

	}
}
