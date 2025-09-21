using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Synqra;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra.Tests.BindingPerformance;

public partial class SamplePublicModel
{
	public partial string Name { get; set; }
	public partial string Property2 { get; set; }
	public partial string Property3 { get; set; }
}

public partial class CommunityMVVMTest : ObservableObject
{
	[ObservableProperty]
	string _roperty2;

	[ObservableProperty]
	public partial string Property3 { get; set; }
}

// Bind 01 BR - Reflection property setter
// Bind 02 BJ - System.Text.Json generators property setter
// Bind 03 BC - Microsoft.Excentions.Configuratuion.Binder generators property setter
// Bind 04 BS - Syncra Generators

public class PerformanceTests : BaseTest
{
	/*
	private partial class SamplePrivateModel
	{
		public string Name { get; set; }
	}
	*/

	[Test]
	public async Task Should_Bind_01_BR_RSet_property()
	{
		int b;
		unsafe
		{
			b = sizeof(nint);
		}
		await Assert.That(b).IsEqualTo(8);

		var obj = new DemoObject();
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

		var ops1 = MeasureOps(() => {
			for (int i = 0; i < 1024; i++)
			{
				obj.Property1 = next();
			}
		});
		await Assert.That(ops1 > 1_000).IsTrue();

		obj.RSetReflection(proName, "test2");
		await Assert.That(obj.Property1).IsEqualTo("test2");

		var ops2 = MeasureOps(() =>
		{
			for (int i = 0; i < 1024; i++)
			{
				obj.RSetReflection(proName, next());
			}
		});
		Assert.Fail($"OPS1={ops1:N} OPS2={ops2:N} D={(ops1 - ops2) / ops1:P}");
		await Assert.That(ops1 > 1_000).IsTrue();

		Assert.Fail(ops2.ToString());
	}

	[Test]
	public async Task Should_Bind_01_BR_RSet_quickly()
	{
		var model = new SamplePublicModel();
		model.RSetReflection("Name", "abc");
		await Assert.That(model.Name).IsEqualTo("abc");

		var ops = MeasureOps(() =>
		{
			for (int i = 0; i < 1024; i++)
			{
				model.RSetReflection("Name", "abc");
			}
		});

		Debug.WriteLine($"RSetGen ops: {ops}");
		await Assert.That(ops).IsGreaterThan(1_000_000);

		Assert.Fail(ops.ToString());
	}

	[Test]
	public async Task Should_Bind_02_BJ_01_Stj_property()
	{
		var obj = new DemoObject();
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

		var ops1 = MeasureOps(() =>
		{
			for (int i = 0; i < 1024; i++)
			{
				obj.Property1 = next();
			}
		});
		await Assert.That(ops1 > 1_000).IsTrue();

		obj.RSetSTJ(proName, "test2", DemoTodo.TestJsonSerializerContext.Default);
		await Assert.That(obj.Property1).IsEqualTo("test2");

		var ops2 = MeasureOps(() =>
		{
			for (int i = 0; i < 1024; i++)
			{
				obj.RSetSTJ(proName, next(), DemoTodo.TestJsonSerializerContext.Default);
			}
		});
		Assert.Fail($"OPS1={ops1:N} OPS2={ops2:N} D={(ops1 - ops2) / ops1:P}");
		await Assert.That(ops2 > 1_000).IsTrue();
	}

	[Test]
	public async Task Should_Bind_02_BJ_02_STJx_property()
	{
		var obj = new DemoObject
		{
			Property1 = "unset",
		};

		var so = new JsonSerializerOptions(DemoTodo.TestJsonSerializerContext.Default.Options);
		var ti = so.GetTypeInfo(typeof(DemoObject));
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
		var ops1 = MeasureOps(() =>
		{
			for (int i = 0; i < 1024; i++)
			{
				JsonSerializer.Deserialize(json2, ti);
			}
		});
		Assert.Fail($"OPS={ops1:N}");
		await Assert.That(ops1 > 1_000).IsTrue();
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

		var ops = MeasureOps(() =>
		{
			for (int i = 0; i < 1024; i++)
			{
				hostBuilder.Configuration.Bind(model);
			}
		});

		Debug.WriteLine($"RSetGen ops: {ops}");
		await Assert.That(ops).IsGreaterThan(1_000_000);

		Assert.Fail(ops.ToString());
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

		var ops = MeasureOps(() =>
		{
			for (int i = 0; i < 1024; i++)
			{
				config.Bind(model);
			}
		});

		Debug.WriteLine($"RSetGen ops: {ops}");
		await Assert.That(ops).IsGreaterThan(1_000_000);

		Assert.Fail(ops.ToString());
	}

	[Test]
	public async Task Should_Bind_03_BC_03_RSetConfigGen_property()
	{
		var model = new SamplePublicModel();
		model.Name = "Value 1";
		await Assert.That(model.Name).IsEqualTo("Value 1");

		model.RSetConfigGen("Name", "Value 2");
		await Assert.That(model.Name).IsEqualTo("Value 2");

		var ops = MeasureOps(() =>
		{
			for (int i = 0; i < 1024; i++)
			{
				model.RSetConfigGen("Name", "Value 2");
			}
		});

		Debug.WriteLine($"RSetGen ops: {ops}");
		await Assert.That(ops).IsGreaterThan(1_000_000);

		Assert.Fail(ops.ToString());
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

		var ops = MeasureOps(() =>
		{
			for (int i = 0; i < 1024; i++)
			{
				bm.Set("Name", next());
			}
		});

		Debug.WriteLine($"RSetGen ops: {ops}");
		await Assert.That(ops).IsGreaterThan(50_000);

		Assert.Fail(ops.ToString());
	}

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

}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public class DemoObject
{
	[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
	public string Property1 { get; set; }
}

public interface IExtendable
{
	[JsonExtensionData]
	IDictionary<string, object> Extra { get; }
}

[Serializable]
public class SettingPropertyException : Exception
{
	public SettingPropertyException()
	{
	}

	public SettingPropertyException(string message) : base(message)
	{
	}

	public SettingPropertyException(string message, Exception inner) : base(message, inner)
	{
	}
}

static class PropertySetterExtensions
{
	/*
	public static string ToPascal(this string name)
	{
		return char.ToUpperInvariant(name[0]) + name.Substring(1);
	}

	public static string ToCamel(this string name)
	{
		return char.ToLowerInvariant(name[0]) + name.Substring(1);
	}
	*/

	public static object RSetSTJ(this object obj, string property, object value, JsonSerializerContext jsonSerializerContext)
	{
		// var tm = GetTypeMetadata(ev.TargetTypeId);
		// var col = GetInternal(tm.Type);
		var so = new JsonSerializerOptions(jsonSerializerContext.Options);
		var ti = so.GetTypeInfo(obj.GetType());

#if DEBUG
		var so2 = new JsonSerializerOptions(jsonSerializerContext.Options);
		var ti2 = so.GetTypeInfo(obj.GetType());
		if (ReferenceEquals(ti2, ti))
		{
			throw new Exception($"Can't get isolated type info");
		}
#endif

		// col.TryGetAttached(ev.TargetId, out var attached);
		ti.CreateObject = () => obj;
		IDictionary<string, object> dic = new Dictionary<string, object>
		{
			[property] = value,
		};
		var json = JsonSerializer.Serialize(dic, jsonSerializerContext.Options);
		var patched = JsonSerializer.Deserialize(json, ti);
		if (!ReferenceEquals(obj, null))
		{
			if (!ReferenceEquals(patched, obj))
			{
				throw new Exception("Failed to patch existing object");
			}
		}
		return Task.CompletedTask;
	}

	public static void RSetConfigGen<TM, TV>(this TM model, string property, TV value)
	{
		var cfg = new TestConfiguration(property, value.ToString());
		cfg.Bind(model);
	}

	public static void RSetConfigGen<TV>(this SamplePublicModel model, string property, TV value)
	{
		var cfg = new TestConfiguration(property, value.ToString());
		cfg.Bind(model);
	}

	[EditorBrowsable(EditorBrowsableState.Never)]
	public static object RSetReflection([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] this object obj, string property, object value, bool autoConvert = false)
	{
		// check for target type
		var pi = obj.GetType().GetProperty(property);
		if (value != null)
		{
			var proType = pi.PropertyType;
			var valType = value.GetType();
			if (proType != valType)
			{
				if (proType.IsGenericType)
				{
					if (proType.GetGenericTypeDefinition() == typeof(Nullable<>))
					{
						proType = proType.GetGenericArguments()[0];
					}
					else
					{
						throw new SettingPropertyException($"Something wrong: are you sure you going to dynamically update {proType.Name} {pi.Name}?");
					}
				}

				if (proType != valType)
				{
					if (proType.IsEnum) // mongo stores enum as int, so have to always allow.
					{
						value = Enum.ToObject(proType, value);
					}
					else if (autoConvert)
					{
						var cvt = Convert.ChangeType(value, proType, CultureInfo.InvariantCulture);
						/*
						// Decimal 2 not object.equal to Int32 2
						if (!Equals(cvt, value))
						{
							throw new Exception($"Failed to convert {valType.Name} {value} to {proType.Name} (got {cvt} and this is not equal)");
						}
						*/
						var cvtTestLoss = Convert.ChangeType(cvt, valType, CultureInfo.InvariantCulture);
						if (!Equals(cvtTestLoss, value) /*|| !Equals(cvtTestLoss, cvt)*/)
						{
							throw new SettingPropertyException($"Failed to convert {valType.Name} {value} to {proType.Name} (got {cvt} and reverse conversion failed, probably data loss)");
						}

						value = cvt;
					}
					else
					{
						throw new SettingPropertyException(
							$"Wrong value type {valType.Name} for field {property}. Expected {proType.Name}");
					}
				}
			}
		}

		pi.SetValue(obj, value);

		return value;
	}
}

class TestConfiguration(string theKey, string theValue) : IConfiguration
{
	TestConfigurationSection? _keyValue;

	public string? this[string key] { get => key == theKey ? theValue : throw new NotImplementedException($"Root has no <{key}> only <{theKey}>"); set => throw new NotImplementedException(); }

	public IEnumerable<IConfigurationSection> GetChildren()
	{
		return [_keyValue ?? (_keyValue = new TestConfigurationSection(theKey, theValue))];
	}

	public IChangeToken GetReloadToken()
	{
		throw new NotImplementedException();
	}

	public IConfigurationSection GetSection(string key)
	{
		return key == theKey ? _keyValue ?? (_keyValue = new TestConfigurationSection(theKey, theValue)) : throw new NotImplementedException();
	}
}

class TestConfigurationSection(string theKey, string theValue) : IConfiguration, IConfigurationSection
{
	public string Key => theKey;
	public string Path => theKey;

	string? IConfigurationSection.Value
	{
		get => theValue;
		set => throw new NotImplementedException();
	}

	public string? this[string key]
	{
		get => throw new NotImplementedException();
		set => throw new NotImplementedException();
	}

	public IEnumerable<IConfigurationSection> GetChildren()
	{
		return [];
	}

	public IChangeToken GetReloadToken()
	{
		throw new NotImplementedException();
	}

	public IConfigurationSection GetSection(string key)
	{
		throw new NotImplementedException();
	}
}

