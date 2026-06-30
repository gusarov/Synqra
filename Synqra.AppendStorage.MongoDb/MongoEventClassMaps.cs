using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
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

			// LINQ filters/projections (e.g. a raw Guid key in Builders<T>.Filter.Eq) resolve their
			// Guid serializer from this global registry, not from any class map's member serializer
			// — a member-scoped fix (the convention below) can't reach query-time serialization at
			// all. TryRegisterSerializer (not RegisterSerializer) so this is a no-op, not a throw,
			// if Security/Jobs/SimpleV1 already claimed the slot first — every feature in this
			// process wants the exact same GuidRepresentation.Standard, so whichever asks first
			// "winning" is fine, unlike RegisterSerializer which throws on a second caller
			// regardless of whether the value would've matched.
			BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
			RegisterSynqraConventions();

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

			// Link.LinkId IS the document's own identity wherever a Link is stored natively as its
			// own document — MongoProjection's "Links" read-model collection. Mapping it to _id (here,
			// once, on the shared base class map every concrete Link subtype's AutoMap extends) avoids
			// a redundant duplicate field (_id and LinkId carrying the exact same value). Not done via
			// a [BsonId] attribute on the model itself: Synqra.Model has no MongoDB dependency, and
			// every other storage backend (in-memory, SBX files) addresses a link by this same LinkId
			// property without needing it singled out as "the" id — that's a Mongo-only document
			// convention. LinkDataSerializer's own well-known-field strip (LinkAddedEvent.Data) accounts
			// for this rename too — see its WellKnown list.
			if (!BsonClassMap.IsClassMapRegistered(typeof(Link)))
			{
				BsonClassMap.RegisterClassMap<Link>(cm =>
				{
					cm.AutoMap();
					cm.MapIdProperty(x => x.LinkId);
				});
			}

			RegisterDerived<ObjectCreatedEvent>("ObjectCreatedEvent");
			RegisterDerived<ObjectPropertyChangedEvent>("ObjectPropertyChangedEvent");
			RegisterDerived<ObjectDeletedEvent>("ObjectDeletedEvent");
			RegisterDerived<CommandCreatedEvent>("CommandCreatedEvent");
			RegisterDerived<ComponentAddedEvent>("ComponentAddedEvent");
			RegisterDerived<ComponentPropertyChangedEvent>("ComponentPropertyChangedEvent");
			RegisterDerived<ComponentDeletedEvent>("ComponentDeletedEvent");
			RegisterDerived<LinkRemovedEvent>("LinkRemovedEvent");

			// LinkAddedEvent gets a member-scoped serializer on Data so the embedded link drops the
			// three well-known fields (_id/LinkId, SourceId, TargetId) it would otherwise duplicate
			// (see LinkDataSerializer). This keeps the Link class map's own field SET untouched —
			// unlike a global convention, so MongoProjection's native link collection can still persist
			// _id/SourceId/TargetId for querying.
			RegisterDerived<LinkAddedEvent>("LinkAddedEvent", cm =>
				cm.GetMemberMap(e => e.Data).SetSerializer(new LinkDataSerializer()));

			_registered = true;
		}
	}

	/// <summary>
	/// Registers conventions applied to <em>any</em> class map BSON builds for Synqra's own
	/// model ecosystem — including ones never explicitly registered here:
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
	/// Attaches an explicit, member-scoped <see cref="GuidSerializer"/>/<see cref="ObjectSerializer"/>
	/// to every <see cref="Guid"/>- and <c>object</c>-typed member (<see cref="GuidAndObjectSerializerConvention"/>).
	/// The MongoDB driver's process-wide defaults require an explicit GUID representation and refuse
	/// to (de)serialize an <c>object</c>-typed member holding a type it doesn't recognize as "safe" —
	/// both confirmed still true on the current driver version, not legacy baggage — but neither needs
	/// a process-wide fix: scoping both choices to Synqra's own member maps via this convention (the
	/// same filter as the other conventions in this pack) gets exactly the same outcome with zero
	/// blast radius outside Synqra's own types. This replaces an earlier approach that reached into
	/// <see cref="BsonSerializer"/>/<see cref="ObjectSerializer"/> internals via reflection and
	/// overwrote them process-wide — confirmed unnecessary by direct experiment: a member-scoped
	/// serializer fully resolves both errors, and an unrelated class map elsewhere in the same process
	/// is unaffected and still gets the driver's strict defaults.
	/// </item>
	/// </list>
	/// <para>
	/// Note: the redundancy where <see cref="LinkAddedEvent.Data"/> (a concrete <c>Link</c>) would
	/// duplicate the three well-known fields the event already carries explicitly is handled NOT here
	/// (a global Link-class-map strip would also clobber <c>MongoProjection</c>'s native link storage,
	/// which needs those fields to query) but by a member-scoped <see cref="LinkDataSerializer"/> on the
	/// <c>Data</c> member alone — see its registration in <see cref="Register"/>. That serializer reuses
	/// this same scoped, wide-open <see cref="ObjectSerializer"/> instance (<see cref="ScopedOpenObjectSerializer"/>)
	/// rather than looking one up ambiently.
	/// </para>
	/// </summary>
	static void RegisterSynqraConventions()
	{
		var pack = new ConventionPack
		{
			new JsonIgnoreConvention(),
			new IgnoreIfNullConvention(true),
			new GuidAndObjectSerializerConvention(),
		};
		// Scoped to Synqra's own model ecosystem — NOT a blanket `_ => true`. That filter
		// previously applied this pack to every class map in the process, including
		// completely unrelated host application types that have nothing to do with Synqra
		// (e.g. Quotaly's JobDefinition), and broke their own Mongo LINQ index/query
		// translation in ways that had nothing to do with Synqra (confirmed: a plain Guid
		// member throwing MongoDB.Driver.Linq.ExpressionNotSupportedException).
		// IBindableModel is what every source-generated [SynqraModel] type implements
		// (TestGraphNode included — narrowing to just Event/Link broke its own durability
		// tests, confirming the convention is genuinely needed for bound models in general,
		// not only the Event/Link hierarchy), so this still covers any consumer-defined
		// model or Link subclass without needing per-type registration — the original
		// reason `_ => true` was used — while excluding everything that isn't Synqra's.
		ConventionRegistry.Register(
			"Synqra.MemberConventions"
			, pack
			, t => typeof(Event).IsAssignableFrom(t) || typeof(Link).IsAssignableFrom(t) || typeof(IBindableModel).IsAssignableFrom(t)
		);
	}

	/// <summary>
	/// Shared, wide-open object serializer — see <see cref="RegisterSynqraConventions"/>. Also used
	/// directly (not via ambient <c>BsonSerializer.LookupSerializer</c>) wherever a model is serialized
	/// with nominal type <c>object</c> to force the <c>_t</c> discriminator — e.g.
	/// <c>MongoProjection.ToDocument</c> — since that path has no class-map member to scope a
	/// convention to, and the driver's own ambient default rejects any type it doesn't recognize.
	/// </summary>
	public static readonly IBsonSerializer ScopedOpenObjectSerializer = new ObjectSerializer(static _ => true);

	/// <summary>Shared, member-scoped <see cref="GuidRepresentation.Standard"/> serializer — see <see cref="RegisterSynqraConventions"/>.</summary>
	static readonly IBsonSerializer ScopedStandardGuidSerializer = new GuidSerializer(GuidRepresentation.Standard);

	/// <summary>
	/// Attaches <see cref="ScopedStandardGuidSerializer"/> to every <see cref="Guid"/>/<c>Guid?</c> member
	/// and <see cref="ScopedOpenObjectSerializer"/> to every <c>object</c>-typed member it sees — scoped to
	/// whichever types the enclosing <see cref="ConventionPack"/> is registered against (Synqra's own
	/// ecosystem), never touching the driver's process-wide defaults. See <see cref="RegisterSynqraConventions"/>.
	/// </summary>
	sealed class GuidAndObjectSerializerConvention : IMemberMapConvention
	{
		public string Name => "Synqra.GuidAndObjectSerializer";

		public void Apply(BsonMemberMap memberMap)
		{
			if (memberMap.MemberType == typeof(Guid) || memberMap.MemberType == typeof(Guid?))
			{
				memberMap.SetSerializer(ScopedStandardGuidSerializer);
			}
			else if (memberMap.MemberType == typeof(object))
			{
				memberMap.SetSerializer(ScopedOpenObjectSerializer);
			}
		}
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

	/// <summary>
	/// Member-scoped BSON serializer for <see cref="LinkAddedEvent.Data"/> (the link instance).
	/// Serializes the link natively through the normal <c>object</c> serializer — so a consumer's
	/// concrete <c>Link</c> subtype keeps its own extra fields and its <c>_t</c> discriminator — then
	/// drops the three well-known fields (<see cref="Link.LinkId"/> — written as <c>_id</c>, since the
	/// shared <c>Link</c> class map maps it as the id member, see <see cref="Register"/> —
	/// <see cref="Link.SourceId"/>/<see cref="Link.TargetId"/>) from the resulting sub-document,
	/// because the event already carries them as explicit top-level fields. Because it's attached to
	/// this one member rather than the <c>Link</c> class map, it never affects how a <c>Link</c> is
	/// serialized anywhere else — notably <c>MongoProjection</c>'s native "Links" collection, which
	/// must keep those very fields to query by endpoint. On replay the materialized link re-stamps
	/// LinkId/SourceId/TargetId from the event's own explicit fields, so the stripped fields are never
	/// read back from Data.
	/// </summary>
	sealed class LinkDataSerializer : SerializerBase<object>
	{
		static readonly string[] WellKnown = ["_id", nameof(Link.SourceId), nameof(Link.TargetId)];

		// Reuses the same scoped, wide-open instance the GuidAndObjectSerializerConvention attaches
		// elsewhere — never the driver's restrictive process-wide default.
		static IBsonSerializer ObjectSerializer => ScopedOpenObjectSerializer;

		public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value)
		{
			if (value is null)
			{
				context.Writer.WriteNull();
				return;
			}

			// Serialize the link to a throwaway document (carries _t + every mapped field), then strip
			// the three the event already owns and write what's left to the real output.
			var doc = new BsonDocument();
			using (var docWriter = new BsonDocumentWriter(doc))
			{
				ObjectSerializer.Serialize(BsonSerializationContext.CreateRoot(docWriter), args, value);
			}
			foreach (var name in WellKnown)
			{
				doc.Remove(name);
			}
			BsonDocumentSerializer.Instance.Serialize(context, args, doc);
		}

		public override object Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
			=> ObjectSerializer.Deserialize(context, args);
	}

	static void RegisterDerived<T>(string discriminator, Action<BsonClassMap<T>>? configure = null)
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
			configure?.Invoke(cm);
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
