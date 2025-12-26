using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Synqra.Tests.Performance;
using Synqra.Tests.SampleModels;
using Synqra.Tests.Helpers;
using Synqra;
using Synqra.Tests.SampleModels.Binding;


namespace Synqra.Tests.Binding;

// Bind 01 BR - Reflection property setter
// Bind 02 BJ - System.Text.Json generators property setter
// Bind 03 BC - Microsoft.Excentions.Configuratuion.Binder generators property setter
// Bind 04 BS - Syncra Generators
public class BindingTests : BaseTest
{

	[Test]
	public async Task Should_00_allow_set_model_properties()
	{
		var model = new SamplePublicModel
		{
			Name = "Test Model",
		};

		await Assert.That(model).IsNotNull();
		await Assert.That(model.Name).IsEqualTo("Test Model");
	}


	[Test]
	public async Task Should_Bind_01_BR_RSet_property()
	{
		int b;
		unsafe
		{
			b = sizeof(nint);
		}
		await Assert.That(b).IsEqualTo(8);

		var obj = new SampleOnePropertyObject();
		var proName = nameof(obj.Property1);

		bool ab = false;
		string stra = "asda";
		string strb = "asdb";

		obj.Property1 = "test1";
		await Assert.That(obj.Property1).IsEqualTo("test1");

		obj.RSetReflection(proName, "test2");
		await Assert.That(obj.Property1).IsEqualTo("test2");
	}

	[Test]
	public async Task Should_Bind_01_BR_RSet()
	{
		var model = new SamplePublicModel();
		model.RSetReflection("Name", "abc");
		// model.RSetReflection("Name", "abc", dynType: typeof(SamplePublicModel));
		await Assert.That(model.Name).IsEqualTo("abc");
	}

	[Test]
	public async Task Should_Bind_02_BJ_01_Stj_property()
	{
		var obj = new SampleOnePropertyObject();
		var proName = nameof(obj.Property1);

		bool ab = false;
		string stra = "asda";
		string strb = "asdb";
		string next()
		{
			ab = !ab;
			return ab ? stra : strb;
		}

		obj.Property1 = "test1";
		await Assert.That(obj.Property1).IsEqualTo("test1");

		obj.RSetSTJ(proName, "test2", SampleJsonSerializerContext.Default);
		await Assert.That(obj.Property1).IsEqualTo("test2");
	}

	[Test]
	public async Task Should_Bind_02_BJ_02_STJx_property()
	{
		var obj = new SampleOnePropertyObject
		{
			Property1 = "unset",
		};

		var so = new JsonSerializerOptions(SampleJsonSerializerContext.DefaultOptions);
		var ti = so.GetTypeInfo(typeof(SampleOnePropertyObject));
		ti.CreateObject = () => obj;
		var json1 = """
{"property1":"xa"}
""";
		var q = JsonSerializer.Deserialize(json1, ti);
		await Assert.That(q).IsSameReferenceAs(obj);

		await Assert.That(obj.Property1).IsEqualTo("xa");

		var json2 = """
{"property1":"xp"}
""";
	}

	[Test]
	public async Task Should_Bind_03_BC_01_HostConfigBind_property()
	{
		var model = new SamplePublicModel();
		model.Name = "Value 1";
		await Assert.That(model.Name).IsEqualTo("Value 1");

		var hostBuilder = Host.CreateApplicationBuilder();
		hostBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
		{
			{ "Name", "Value 2" }
		});
		hostBuilder.Services.Configure<SamplePublicModel>(hostBuilder.Configuration);
		var host = hostBuilder.Build();
		hostBuilder.Configuration.Bind(model);
		await Assert.That(model.Name).IsEqualTo("Value 2");
	}

	[Test]
	public async Task Should_Bind_03_BC_02_ConfigBind_property()
	{
		var model = new SamplePublicModel();
		model.Name = "Value 1";
		await Assert.That(model.Name).IsEqualTo("Value 1");

		var config = new TestConfiguration("Name", "Value 2");
		config.Bind(model);
		await Assert.That(model.Name).IsEqualTo("Value 2");
	}

	[Test]
	public async Task Should_Bind_03_BC_03_RSetConfigGen_property()
	{
		var model = new SamplePublicModel();
		model.Name = "Value 1";
		await Assert.That(model.Name).IsEqualTo("Value 1");

		model.RSetConfigGen("Name", "Value 2");
		await Assert.That(model.Name).IsEqualTo("Value 2");
	}

	[Test]
	public async Task Should_Bind_04_BS_01_Generate_BindableModel()
	{
		var model = new SamplePublicModel();
		model.Name = "Value 1";
		await Assert.That(model.Name).IsEqualTo("Value 1");

		var bm = (IBindableModel)model;
		bm.Set("Name", "Value 2");
		await Assert.That(model.Name).IsEqualTo("Value 2");

		bool ab = false;
		string stra = "asda";
		string strb = "asdb";
		string next()
		{
			ab = !ab;
			return ab ? stra : strb;
		}
	}

}
