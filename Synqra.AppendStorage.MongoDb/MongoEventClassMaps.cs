using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// Registers native BSON class maps for the <see cref="Event"/> hierarchy so the
/// Mongo event log stores events as first-class, queryable BSON documents (not
/// opaque blobs).
/// <para>
/// Polymorphism uses MongoDB's default discriminator element (<c>_t</c>) with the
/// same discriminator <em>values</em> the System.Text.Json log uses
/// (<c>"WireAddedEvent"</c>, …). Keeping the field name and the values aligned means
/// a document is self-describing and reads identically whether it came through the
/// JSON-lines log or Mongo — and a future migration between the two is a copy, not a
/// transform.
/// </para>
/// <para>
/// <see cref="Event.EventId"/> maps to <c>_id</c> (the natural document key).
/// <see cref="Event.StreamId"/> is intentionally unmapped: like the JSON log, the
/// stream/container id is an out-of-band routing concern, not part of the persisted
/// event body.
/// </para>
/// </summary>
public static class MongoEventClassMaps
{
	static readonly object _gate = new();
	static bool _registered;

	/// <summary>
	/// Idempotently register the Event hierarchy class maps. Safe to call multiple
	/// times and from multiple threads; MongoDB throws if a class map is registered
	/// twice, so registration is guarded.
	/// </summary>
	public static void Register()
	{
		if (_registered)
		{
			return;
		}
		lock (_gate)
		{
			if (_registered)
			{
				return;
			}

			// MongoDB driver 3.x requires an explicit GUID representation. Standard
			// (BSON binary subtype 4) is the portable, modern encoding — the right
			// choice for a durable log that must read identically across drivers and
			// languages. Without this, GuidSerializer throws "GuidRepresentation is
			// Unspecified" on the first event written.
			BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

			if (!BsonClassMap.IsClassMapRegistered(typeof(Event)))
			{
				BsonClassMap.RegisterClassMap<Event>(cm =>
				{
					cm.AutoMap();
					cm.SetIsRootClass(true);
					cm.MapIdProperty(e => e.EventId);
					// StreamId (out-of-band routing) and any other [JsonIgnore] field are not
					// part of the persisted event body — drop them, matching the JSON log.
					UnmapJsonIgnored(cm);
				});
			}

			RegisterDerived<ObjectCreatedEvent>("ObjectCreatedEvent");
			RegisterDerived<ObjectPropertyChangedEvent>("ObjectPropertyChangedEvent");
			RegisterDerived<ObjectDeletedEvent>("ObjectDeletedEvent");
			RegisterDerived<CommandCreatedEvent>("CommandCreatedEvent");
			RegisterDerived<ComponentAddedEvent>("ComponentAddedEvent");
			RegisterDerived<ComponentPropertyChangedEvent>("ComponentPropertyChangedEvent");
			RegisterDerived<ComponentDeletedEvent>("ComponentDeletedEvent");
			RegisterDerived<WireAddedEvent>("WireAddedEvent");
			RegisterDerived<WireDeletedEvent>("WireDeletedEvent");

			_registered = true;
		}
	}

	static void RegisterDerived<T>(string discriminator)
		where T : Event
	{
		if (BsonClassMap.IsClassMapRegistered(typeof(T)))
		{
			return;
		}
		BsonClassMap.RegisterClassMap<T>(cm =>
		{
			cm.AutoMap();
			cm.SetDiscriminator(discriminator);
			// e.g. ObjectCreatedEvent.DataObject is the in-memory materialized object,
			// marked [JsonIgnore] — it must not be persisted into the durable log.
			UnmapJsonIgnored(cm);
		});
	}

	/// <summary>
	/// Drop every member this class map declares that the model marks
	/// <see cref="JsonIgnoreAttribute"/>, so the durable Mongo log persists exactly the
	/// same surface as the JSON log. Only declared members are considered — inherited
	/// ones (e.g. <c>Event.StreamId</c>) are owned by the base class map.
	/// </summary>
	static void UnmapJsonIgnored(BsonClassMap cm)
	{
		foreach (var memberMap in cm.DeclaredMemberMaps.ToArray())
		{
			if (memberMap.MemberInfo.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true).Length > 0)
			{
				cm.UnmapMember(memberMap.MemberInfo);
			}
		}
	}
}
