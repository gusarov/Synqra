using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace Synqra;

public static class ComponentMatchExtensions
{
	/// <summary>The property a match names, on a registered type; an unknown type or property throws, naming both, so a typo never matches nothing.</summary>
	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "A property trimmed away is not matchable; the registered model types keep their public properties.")]
	public static PropertyInfo ResolveProperty(this ComponentMatch match, ITypeMetadataProvider types)
	{
		PropertyInfo property;
		if (!types.RegisteredTypes.Any(m => m.Type == match.Component))
		{
			throw new ArgumentException($"'{match.Component.Name}' is not a registered type; a match names a registered component type.", nameof(match));
		}
		else
		{
			property = match.Component.GetProperty(match.Property, BindingFlags.Public | BindingFlags.Instance)
				?? throw new ArgumentException($"'{match.Component.Name}' has no property '{match.Property}'.", nameof(match));
		}
		return property;
	}

	/// <summary>The match value as the property's own type (a long for an int member), so an equality holds by value; a value the type cannot take throws.</summary>
	public static object? CoerceValue(this ComponentMatch match, PropertyInfo property)
	{
		var memberType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
		object? value = match.Value;
		if (value is IConvertible convertible && value.GetType() != memberType && memberType != typeof(object) && !memberType.IsEnum)
		{
			value = convertible.ToType(memberType, CultureInfo.InvariantCulture);
		}
		return value;
	}
}
