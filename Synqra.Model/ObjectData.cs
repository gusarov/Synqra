using System.Linq;

namespace Synqra;

/// <summary>
/// Canonical property-bag shape for the payload of object-creation commands/events
/// (<see cref="CreateObjectCommand.Data"/>, <see cref="ObjectCreatedEvent.Data"/>).
/// Backed by <see cref="Dictionary{TKey, TValue}"/> (rather than an ordered list) so
/// it stays compatible with per-property "extra"/overflow handling later, and so the
/// SBX binary serializer's existing <c>IDictionary&lt;string, object?&gt;</c> detection
/// picks it up without bespoke generator support.
/// </summary>
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
	/// readable+writable properties and skipping ones still at their type's default value.
	/// This is the single normalization site; consumers (reducers, projections) only ever see
	/// the resulting dictionary and never need to branch on the original input's shape.
	/// </summary>
	public static ObjectData From(object source)
	{
		switch (source)
		{
			case null:
				throw new ArgumentNullException(nameof(source));
			case ObjectData already:
				return already;
			case IDictionary<string, object?> dict:
				return new ObjectData(dict);
			default:
				var result = new ObjectData();
				foreach (var pro in source.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
				{
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
}
