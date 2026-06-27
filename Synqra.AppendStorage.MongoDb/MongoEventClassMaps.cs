using System.Reflection;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// Registers native BSON class maps for the <see cref="Event"/> hierarchy so the
/// Mongo event log stores events as first-class, queryable BSON documents (not
/// opaque blobs).
/// <para>
/// Polymorphism uses MongoDB's default <em>scalar</em> discriminator element (<c>_t</c>)
/// holding a single type name (e.g. <c>"ObjectPropertyChangedEvent"</c>) — the same field
/// name and values the System.Text.Json log uses. (We deliberately don't mark the base a
/// root class, which would switch <c>_t</c> to the hierarchical type-chain array; we never
/// query the log by base type, so the scalar form is enough and matches the JSON log.)
/// A document is therefore self-describing and reads identically whether it came through
/// the JSON-lines log or Mongo — a future migration between the two is a copy, not a transform.
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

			PatchObjectSerializerDefaults();
			RegisterJsonIgnoreConvention();

			if (!BsonClassMap.IsClassMapRegistered(typeof(Event)))
			{
				BsonClassMap.RegisterClassMap<Event>(cm =>
				{
					cm.AutoMap();
					// NOTE: intentionally NOT SetIsRootClass(true). Root classes opt the
					// hierarchy into MongoDB's hierarchical discriminator, which writes _t as
					// the whole type chain (["Event","ObjectPropertyChangedEvent"]). We don't
					// query the log by base type, so the default scalar discriminator
					// (_t: "ObjectPropertyChangedEvent") is enough — smaller, and identical to
					// the scalar _t the JSON-lines log uses. Discriminator-required so _t is
					// always present even if an event is ever serialized as its concrete type.
					cm.SetDiscriminatorIsRequired(true);
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

			_registered = true;
		}
	}

	/// <summary>
	/// Registers two global conventions applied to <em>any</em> class map BSON builds — including
	/// ones never explicitly registered here:
	/// <list type="bullet">
	/// <item>
	/// Strips every <see cref="JsonIgnoreAttribute"/>-marked member. Without this, a dynamic
	/// <c>object</c>-typed event member that carries a live model instance (e.g.
	/// <see cref="LinkAddedEvent.Data"/> holding a concrete <c>Link</c> subclass) gets auto-mapped
	/// <em>implicitly</em> the first time BSON encounters it, and that implicit auto-map never runs
	/// <see cref="UnmapJsonIgnored"/> — only the types this class explicitly calls
	/// <see cref="RegisterDerived{T}"/> for get that treatment. A consumer-defined Link subclass is
	/// exactly such a type: the framework has no way to know about it ahead of time, so per-type
	/// registration can't cover it, but a convention applies to every AutoMap call, explicit or
	/// implicit, uniformly.
	/// </item>
	/// <item>
	/// Skips writing a member at all when its value is null (the driver's built-in
	/// <see cref="IgnoreIfNullConvention"/>). Most event fields that aren't always populated for a
	/// given event kind (e.g. <see cref="ObjectCreatedEvent.Data"/>, never set by the current
	/// command-handling path) are nullable reference types, so without this every document carries
	/// an explicit <c>"Field": null</c> for each one that happens not to apply.
	/// </item>
	/// <item>
	/// Strips <see cref="Link.LinkId"/>/<see cref="Link.SourceId"/>/<see cref="Link.TargetId"/> from
	/// the <see cref="Link"/> base's own class map — which is what every concrete link subtype's
	/// auto-mapped chain shares (BSON builds one class map per level of the inheritance chain, and
	/// caches/reuses <c>typeof(Link)</c>'s). Without this, <see cref="LinkAddedEvent.Data"/> (a
	/// concrete <c>Link</c> instance, e.g. <c>HierarchyLink</c>) would duplicate the exact three
	/// fields <see cref="LinkAddedEvent"/> itself already carries explicitly, persisted twice in the
	/// same document. Safe to strip universally: nothing else round-trips a <c>Link</c> through this
	/// native-BSON path — <c>MongoProjection</c>'s own "Links" collection storage goes through
	/// <c>System.Text.Json</c> instead, an entirely separate serializer this convention never touches.
	/// On replay, the materialized link's <c>LinkId</c>/<c>SourceId</c>/<c>TargetId</c> are always
	/// re-stamped from the event's own explicit fields anyway (see <c>VisitLinkAddedCore</c>), so the
	/// stripped fields were never read back even before this fix — purely dead weight on disk.
	/// </item>
	/// </list>
	/// </summary>
	static void RegisterJsonIgnoreConvention()
	{
		var pack = new ConventionPack { new JsonIgnoreConvention(), new IgnoreIfNullConvention(true), new LinkWellKnownFieldsConvention() };
		ConventionRegistry.Register("Synqra.JsonIgnoreAndSkipNulls", pack, _ => true);
	}

	sealed class JsonIgnoreConvention : IClassMapConvention
	{
		public string Name => "Synqra.JsonIgnore";

		public void Apply(BsonClassMap classMap)
		{
			foreach (var memberMap in classMap.DeclaredMemberMaps.ToArray())
			{
				if (memberMap.MemberInfo.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true).Length > 0)
				{
					classMap.UnmapMember(memberMap.MemberInfo);
				}
			}
		}
	}

	sealed class LinkWellKnownFieldsConvention : IClassMapConvention
	{
		public string Name => "Synqra.LinkWellKnownFields";

		public void Apply(BsonClassMap classMap)
		{
			if (classMap.ClassType != typeof(Link))
			{
				return;
			}
			foreach (var memberMap in classMap.DeclaredMemberMaps.ToArray())
			{
				classMap.UnmapMember(memberMap.MemberInfo);
			}
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

	/// <summary>
	/// Forces two ambient MongoDB defaults that the public API cannot reliably change once the
	/// process has started, by reaching into <see cref="BsonSerializer"/>/<see cref="ObjectSerializer"/>
	/// internals and overwriting them directly rather than asking nicely. Both fixes are needed for
	/// any <c>object</c>-typed event member that carries a live, concrete payload — e.g.
	/// <see cref="LinkAddedEvent.Data"/> (the link instance itself, mirroring
	/// <see cref="ComponentAddedEvent.Data"/>'s contract) or a boxed <see cref="Guid"/> like
	/// <see cref="ObjectPropertyChangedEvent.NewValue"/>:
	/// <list type="number">
	/// <item>
	/// <b>GUID representation.</b> MongoDB driver 3.x requires an explicit one; the obvious approach
	/// — <c>BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard))</c> —
	/// does not work reliably. The global registry lazily self-registers a default
	/// <c>GuidSerializer(Unspecified)</c> the moment ANYTHING in the process first asks for one
	/// (observed in practice: a driver-internal admin command from test infrastructure spinning up
	/// a database, well before this method ever runs), and the public API refuses to register a
	/// different one over an existing registration — first ask wins, permanently. Overwriting the
	/// registry's backing dictionary entry directly sidesteps that race: it doesn't matter who got
	/// there first, because this replaces whatever is there.
	/// </item>
	/// <item>
	/// <b>Type allowlist.</b> The default <see cref="ObjectSerializer"/> refuses to (de)serialize any
	/// type it doesn't recognize as "safe" — a deliberate anti-gadget-attack default aimed at
	/// untrusted input, which an event-sourcing log's own application-defined payload types are not.
	/// Constructing a replacement with both allowed-type predicates open and patching it in the same
	/// way covers this.
	/// </item>
	/// </list>
	/// <para>
	/// Each <c>ObjectSerializer</c> instance carries its own copies of both — a private
	/// <c>_guidSerializer</c> field and the allowed-type predicates — fixed at construction, never
	/// re-read from the registry afterward. And there can be multiple instances already cached
	/// (confirmed empirically: <c>ObjectSerializer.Instance</c> and whatever the registry separately
	/// resolved for <c>typeof(object)</c> are NOT the same object), so a single freshly-constructed,
	/// fully-open replacement is force-installed everywhere an instance could come from: the
	/// registry's <c>typeof(object)</c> slot, the <c>ObjectSerializer.Instance</c> static field
	/// itself, and any other <c>ObjectSerializer</c> already sitting in the registry cache.
	/// </para>
	/// <para>
	/// Reflects into <see cref="BsonSerializer"/>/<see cref="BsonSerializerRegistry"/>/
	/// <see cref="ObjectSerializer"/> internals — all private and unsupported, so a driver upgrade
	/// could rename or restructure them. If that happens this degrades to a no-op (caught and
	/// ignored) rather than a crash; the symptom would be the same "GuidRepresentation is
	/// Unspecified" / "is not configured as a type that is allowed to be serialized" exceptions this
	/// method exists to prevent, at which point the field names below need updating for the new
	/// driver version.
	/// </para>
	/// </summary>
	static void PatchObjectSerializerDefaults()
	{
		try
		{
			var standardGuid = new GuidSerializer(GuidRepresentation.Standard);
			var openPredicate = (Func<Type, bool>)(static _ => true);

			var guidSerializerField = typeof(ObjectSerializer).GetField("_guidSerializer", BindingFlags.NonPublic | BindingFlags.Instance);
			var allowedSerializeField = typeof(ObjectSerializer).GetField("_allowedSerializationTypes", BindingFlags.NonPublic | BindingFlags.Instance);
			var allowedDeserializeField = typeof(ObjectSerializer).GetField("_allowedDeserializationTypes", BindingFlags.NonPublic | BindingFlags.Instance);

			// Mutate fields on the EXISTING instance(s) in place — instance fields stay mutable via
			// reflection even when readonly, unlike a `static readonly` field (see below). This is
			// the only thing that actually works: replacing ObjectSerializer's `static readonly
			// __instance` field throws FieldAccessException ("Cannot set initonly static field
			// ... after type is initialized") under this runtime, so swapping in a freshly
			// constructed instance is not an option — every already-existing ObjectSerializer has
			// to be patched where it stands.
			void PatchInstance(ObjectSerializer instance)
			{
				guidSerializerField?.SetValue(instance, standardGuid);
				allowedSerializeField?.SetValue(instance, openPredicate);
				allowedDeserializeField?.SetValue(instance, openPredicate);
			}

			PatchInstance(ObjectSerializer.Instance);

			var registry = typeof(BsonSerializer)
				.GetField("__serializerRegistry", BindingFlags.NonPublic | BindingFlags.Static)?
				.GetValue(null);
			var cache = registry?.GetType()
				.GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)?
				.GetValue(registry) as System.Collections.Concurrent.ConcurrentDictionary<Type, IBsonSerializer>;
			if (cache is not null)
			{
				cache[typeof(Guid)] = standardGuid;
				// Patch whatever ObjectSerializer instance(s) are already cached under some other
				// type key (e.g. a closed-over instance from before this method ran), and make sure
				// the registry's own typeof(object) slot holds a patched instance too — calling
				// GetOrAdd-style LookupSerializer for the first time would otherwise construct (and
				// cache) a fresh, restricted instance this method never gets to touch.
				if (!cache.TryGetValue(typeof(object), out var existingObjectSerializer) || existingObjectSerializer is not ObjectSerializer)
				{
					cache[typeof(object)] = ObjectSerializer.Instance;
				}
				foreach (var cached in cache.Values)
				{
					if (cached is ObjectSerializer os)
					{
						PatchInstance(os);
					}
				}
			}
		}
		catch
		{
			// Best-effort: see the "unsupported reflection" remarks above.
		}
	}
}
