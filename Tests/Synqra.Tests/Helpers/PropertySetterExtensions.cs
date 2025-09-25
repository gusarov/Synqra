using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.Performance;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Synqra.Tests.DemoTodo;

namespace Synqra.Tests.Helpers;

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

	[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public static object RSetReflection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(this T obj, string property, object value, bool autoConvert = false)
	{
		if (obj == null)
		{
			throw new ArgumentNullException(nameof(obj));
		}

		var pi = typeof(T).GetProperty(property, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
		if (pi == null)
		{
			var type = obj.GetType();
			pi = type.GetProperty(property, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public); // this will help to discover a concrete type member in runtime, but not in NativeAOT!
			if (pi == null)
			{
				throw new SettingPropertyException($"No property {property} found on type {type.Name}");
			}
		}

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
						var cvtTestLoss = Convert.ChangeType(cvt, valType, CultureInfo.InvariantCulture);
						if (!Equals(cvtTestLoss, value))
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

