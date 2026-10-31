using System.Reflection;
using Synqra.BinarySerializer;

namespace Synqra;

/// <summary>
/// The schema header of a stream snapshot (plans/snapshot.md §2). SBX writes no schema version on the wire, so a reader on
/// another build would decode with its own latest layout; the header pins every type the payload uses — SBX id, Synqra type
/// id, schema version and signature — and the reader is built from it, or the snapshot is refused whole.
/// </summary>
public sealed class SnapshotSchema(ISbxSerializerFactory serializerFactory)
{
	/// <summary>The record layout and the snapshot frames this build writes and reads.</summary>
	public long FormatVersion => 1;

	/// <summary>A payload writer: this host's own serializer, mapped as the factory maps it.</summary>
	public SbxSerializer CreateWriter()
		=> serializerFactory.CreateSerializer() as SbxSerializer
			?? throw new InvalidOperationException("A snapshot header describes SbxSerializer mappings; the registered serializer factory makes another kind.");

	/// <summary>The header for a snapshot written with <paramref name="writer"/>: every type it maps, at the version it writes.</summary>
	public SnapshotBegin DescribeSnapshot(SbxSerializer writer, Guid streamId, Guid snapshotId, Guid watermark, long entityCount)
		=> new SnapshotBegin
		{
			FormatVersion = FormatVersion,
			StreamId = streamId,
			SnapshotId = snapshotId,
			Watermark = watermark,
			TimeBase = new DateTimeOffset(writer.TimeBase).ToUnixTimeSeconds(),
			Types = writer.Mappings
				.OrderBy(x => x.MapId)
				.Select(x => new SnapshotType
				{
					MapId = x.MapId,
					TypeId = ResolveTypeId(x.Type),
					Version = x.SchemaVersion,
					Schema = FindSignature(x.Type, x.SchemaVersion)?.Schema,
				})
				.ToList(),
			EntityCount = entityCount,
		};

	/// <summary>
	/// A payload reader mapped exactly as <paramref name="header"/> says. Refused whole when the format is not this build's,
	/// or an entry names a type this build does not map or a schema it does not have.
	/// </summary>
	public SbxSerializer CreateReader(SnapshotBegin header)
	{
		if (header.FormatVersion != FormatVersion)
		{
			throw new SnapshotSchemaRefusedException($"Snapshot format {header.FormatVersion} is not this build's format {FormatVersion}.", null);
		}
		var known = CreateWriter().Mappings
			.GroupBy(x => ResolveTypeId(x.Type))
			.ToDictionary(x => x.Key, x => x.First());
		var reader = new SbxSerializer();
		reader.SetTimeBase(DateTimeOffset.FromUnixTimeSeconds(header.TimeBase).UtcDateTime);
		foreach (var entry in header.Types)
		{
			if (!known.TryGetValue(entry.TypeId, out var mapping))
			{
				throw new SnapshotSchemaRefusedException($"Snapshot type {entry} is not mapped by this build.", entry);
			}
			else if (!Accepts(mapping, entry))
			{
				throw new SnapshotSchemaRefusedException($"Snapshot type {entry} ({mapping.Type.FullName}) has a schema this build cannot read.", entry);
			}
			else
			{
				reader.Map((int)entry.MapId, entry.Version, mapping.Type, mapping.Binder);
			}
		}
		reader.Snapshot(); // each chunk resets to here
		return reader;
	}

	// The exact signature at that version, or — for a type without one — the very version this build maps it at.
	bool Accepts(SbxMapping mapping, SnapshotType entry)
	{
		var signature = FindSignature(mapping.Type, (float)entry.Version);
		return signature is not null
			? signature.Schema == entry.Schema
			: entry.Schema is null && mapping.SchemaVersion == (float)entry.Version;
	}

	// The type's own [Schema] first, then its bases' (a type may inherit its layout, e.g. ObjectDeletedEvent).
	SchemaAttribute? FindSignature(Type type, float version)
	{
		SchemaAttribute? found = null;
		for (var current = type; current is not null && found is null; current = current.BaseType)
		{
			found = current.GetCustomAttributes<SchemaAttribute>(inherit: false).FirstOrDefault(x => (float)x.Version == version);
		}
		return found;
	}

	// As TypeMetadataProvider derives it; a generic type by its runtime-independent name, not its assembly-qualified one.
	Guid ResolveTypeId(Type type)
		=> type.GetCustomAttribute<SynqraModelAttribute>()?.SynqraTypeId
			?? GuidExtensions.CreateVersion5(SynqraGuids.SynqraTypeNamespaceId, type.IsGenericType ? type.ToString() : type.FullName!);
}
