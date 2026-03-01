using System.Runtime.InteropServices;

namespace Synqra;

public static class SynqraTypeExtensions
{
	private static Dictionary<Type, object> _defaults = new Dictionary<Type, object>();

	public static object? GetDefault(this Type type)
	{
		if (!type.IsValueType)
		{
			return null;
		}
		if (!_defaults.TryGetValue(type, out var value))
		{
			lock (_defaults)
			{
				if (!_defaults.TryGetValue(type, out value))
				{
					_defaults[type] = value = Activator.CreateInstance(type);
				}
			}
		}
		return value;
	}
}