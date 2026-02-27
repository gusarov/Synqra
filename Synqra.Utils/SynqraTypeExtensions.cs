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
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_defaults, type, out _);
		if (slot == null)
		{
			slot = Activator.CreateInstance(type);
		}
		return slot;
#else
		return null;
#endif
	}
}