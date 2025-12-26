using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.Tests.Performance;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Synqra.Tests.SampleModels;
using Synqra.Tests.Helpers;
using Synqra.Tests.SampleModels.Binding;

namespace Synqra.Tests.Performance;

// Bind 01 BR - Reflection property setter
// Bind 02 BJ - System.Text.Json generators property setter
// Bind 03 BC - Microsoft.Excentions.Configuratuion.Binder generators property setter
// Bind 04 BS - Syncra Generators
[Category("Performance")]
[Property("CI", "false")]
[NotInParallel]
// [Explicit]
public class PerformanceTests : BaseTest
{
	/*
	private partial class SamplePrivateModel
	{
		public string Name { get; set; }
	}
	*/

	[Test]
	public async Task Should_Bind_faster_than_reflection_and_faster_than_minimum_expectations()
	{
		// Reflection

		var model = new SamplePublicModel();
		model.RSetReflection("Name", "abc");
		await Assert.That(model.Name).IsEqualTo("abc");

		var reflectionOps = MeasureOps(() => { model.RSetReflection("Name", "abc"); });

		Debug.WriteLine($"RSetReflection ops: {reflectionOps}");
		await Assert.That(reflectionOps).IsGreaterThan(1_000_000);

		var bm = (IBindableModel)model;
		bm.Set("Name", "def");
		await Assert.That(model.Name).IsEqualTo("def");
		var ops = MeasureOps(() => { bm.Set("Name", "def"); });

		Debug.WriteLine($"IBindableModel.Set ops: {ops}");
#if NET9_0_OR_GREATER
		await Assert.That(ops).IsGreaterThan(20_000_000);
#else
		await Assert.That(ops).IsGreaterThan(1_000);
#endif
		await Assert.That(ops).IsGreaterThan(reflectionOps);
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

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		string next()
		{
			ab = !ab;
			return ab ? stra : strb;
		}

		obj.Property1 = "test1";
		await Assert.That(obj.Property1).IsEqualTo("test1");

		var ops1 = MeasureOps(() => obj.Property1 = next());
		await Assert.That(ops1 > 1_000_000).IsTrue();

		obj.RSetReflection(proName, "test2");
		await Assert.That(obj.Property1).IsEqualTo("test2");

		var ops2 = MeasureOps(() => obj.RSetReflection(proName, next()));

		Console.WriteLine($"OPS1={ops1:N} OPS2={ops2:N} D={(ops1 - ops2) / ops1:P}");
		await Assert.That(ops1 > 1_000_000).IsTrue();

		// Assert.Fail(ops2.ToString());
	}

	[Test]
	public async Task Should_Bind_01_BR_RSet_quickly()
	{
		var model = new SamplePublicModel();
		model.RSetReflection("Name", "abc");
		await Assert.That(model.Name).IsEqualTo("abc");

		var ops = MeasureOps(() => model.RSetReflection("Name", "abc"));

		Debug.WriteLine($"RSetReflection ops: {ops}");
		await Assert.That(ops).IsGreaterThan(1_000_000);

		// Assert.Fail(ops.ToString());
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

		var ops1 = MeasureOps(() => obj.Property1 = next());
		await Assert.That(ops1 > 1_000_000).IsTrue();

		obj.RSetSTJ(proName, "test2", SampleJsonSerializerContext.Default);
		await Assert.That(obj.Property1).IsEqualTo("test2");

		var ops2 = MeasureOps(() => obj.RSetSTJ(proName, next(), SampleJsonSerializerContext.Default));
		Console.WriteLine($"OPS1={ops1:N} OPS2={ops2:N} D={(ops1 - ops2) / ops1:P}");
		await Assert.That(ops2 > 10).IsTrue();
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
		var ops1 = MeasureOps(() => JsonSerializer.Deserialize(json2, ti));
		await Assert.That(ops1 > 1_000_000).IsTrue();
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

		var ops = MeasureOps(() => hostBuilder.Configuration.Bind(model));

		await Assert.That(ops).IsGreaterThan(1_000_000);
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

		var ops = MeasureOps(() => config.Bind(model));

		await Assert.That(ops).IsGreaterThan(1_000_000);
	}

	[Test]
	public async Task Should_Bind_03_BC_03_RSetConfigGen_property()
	{
		var model = new SamplePublicModel();
		model.Name = "Value 1";
		await Assert.That(model.Name).IsEqualTo("Value 1");

		model.RSetConfigGen("Name", "Value 2");
		await Assert.That(model.Name).IsEqualTo("Value 2");

		var ops = MeasureOps(() => model.RSetConfigGen("Name", "Value 2"));

		await Assert.That(ops).IsGreaterThan(1_000_000);
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

		var ops = MeasureOps(() => bm.Set("Name", next()), new PerformanceParameters
		{
			TotalTargetTime = TimeSpan.FromSeconds(10)
		});

		Debug.WriteLine($"RSetGen ops: {ops}");
		await Assert.That(ops).IsGreaterThan(1_000_000);

		// Assert.Fail(ops.ToString());
	}

	[Test]
	public async Task Should_Compare()
	{

	}
}

