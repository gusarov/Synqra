using System.Linq;
using System.Text.Json.Serialization;

namespace Synqra;

/// <summary>
/// Canonical property-bag shape for the payload of object-creation commands/events
/// (<see cref="CreateObjectCommand.Data"/>, <see cref="ObjectCreatedEvent.Data"/>).
/// Backed by <see cref="Dictionary{TKey, TValue}"/> (rather than an ordered list) so
/// it stays compatible with per-property "extra"/overflow handling later, and so the
/// SBX binary serializer's existing <c>IDictionary&lt;string, object?&gt;</c> detection
/// picks it up without bespoke generator support.
/// </summary>
[JsonConverter(typeof(ObjectDataJsonConverter))]
public sealed class ObjectData : Dictionary<string, object?>
{
	public ObjectData()
	{
	}

	public ObjectData(IDictionary<string, object?> source) : base(source)
	{
	}

	public ObjectData(IEnumerable<KeyValuePair<string, object?>> source)
	{
		foreach (var kvp in source)
		{
			this[kvp.Key] = kvp.Value;
		}
	}

	/// <summary>
	/// Normalizes a creation payload into the canonical property-bag shape. Accepts an
	/// already-canonical <see cref="ObjectData"/>, any other <see cref="IDictionary{String, Object}"/>,
	/// or a POCO/anonymous-object/<see cref="IBindableModel"/> instance — reflecting its
	/// readable+writable, non-<see cref="JsonIgnoreAttribute"/> properties and skipping ones still
	/// at their type's default value. This is the single normalization site; consumers (reducers,
	/// projections) only ever see the resulting dictionary and never need to branch on the original
	/// input's shape.
	/// </summary>
	/// <param name="exclude">
	/// Property names to omit even if present on <paramref name="source"/> — for fields that are
	/// already explicit, structural members elsewhere on the wrapping command/event (e.g. a link's
	/// own <c>LinkId</c>/<c>SourceId</c>/<c>TargetId</c>, already top-level fields on
	/// <c>AddLinkCommand</c>/<c>LinkAddedEvent</c>; carrying them again inside the bag would just be
	/// a second, driftable copy of the same value).
	/// </param>
	public static ObjectData From(object source, ISet<string>? exclude = null)
	{
		switch (source)
		{
			case null:
				throw new ArgumentNullException(nameof(source));
			case ObjectData already:
				return exclude is null ? already : Filtered(already, exclude);
			case IDictionary<string, object?> dict:
				return Filtered(dict, exclude);
			default:
				var result = new ObjectData();
				foreach (var pro in source.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
				{
					if (exclude?.Contains(pro.Name) == true || HasJsonIgnore(pro))
					{
						continue;
					}
					var value = pro.GetValue(source);
					if (Equals(value, pro.PropertyType.GetDefault()))
					{
						continue;
					}
					result[pro.Name] = value;
				}
				return result;
		}
	}

	static ObjectData Filtered(IEnumerable<KeyValuePair<string, object?>> source, ISet<string>? exclude)
	{
		var result = new ObjectData();
		foreach (var kvp in source)
		{
			if (exclude?.Contains(kvp.Key) != true)
			{
				result[kvp.Key] = kvp.Value;
			}
		}
		return result;
	}

	static bool HasJsonIgnore(System.Reflection.PropertyInfo property) =>
		property.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true).Length > 0;
}
