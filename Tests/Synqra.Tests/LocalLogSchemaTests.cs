using Synqra.AppendStorage;
using Synqra.AppendStorage.BlobStorage;
using Synqra.BinarySerializer;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// A browser's local log outlives the build that wrote it. Its records used to carry no schema version, so a new build read every
/// record at its own latest schema: Quotaly's IconComponent gained two members (2026.500 → 2026.873) and every older record holding
/// one ran past its end on read, an IndexOutOfRangeException that left the owner's page on "Opening your things…" (2026-11-17).
/// </summary>
public class LocalLogSchemaTests
{
	const int MapId = 246;
	readonly Guid _stream = Guid.NewGuid();
	readonly SharedBrowserLogTests.SharedTailStore _store = new SharedBrowserLogTests.SharedTailStore();

	[Test]
	public async Task Should_read_a_record_the_previous_build_wrote_under_an_older_schema()
	{
		var previous = new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, 2026.500, typeof(TestEvolvedComponent))));
		await previous.AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "🔥" }));

		var current = new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))));
		var read = await ReadAllAsync(current);

		var icon = (TestEvolvedComponent)((ComponentAddedEvent)read.Single()).Data!;
		await Assert.That(icon.Glyph).IsEqualTo("🔥");
		await Assert.That(icon.IconKey).IsNull();
		await Assert.That(icon.IconId).IsEqualTo(Guid.Empty);
	}

	[Test]
	public async Task Should_still_write_and_read_at_the_latest_schema()
	{
		var current = new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))));
		var iconId = Guid.NewGuid();
		await current.AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "g", IconKey = "build-agent", IconId = iconId }));

		var read = await ReadAllAsync(new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent)))));

		var icon = (TestEvolvedComponent)((ComponentAddedEvent)read.Single()).Data!;
		await Assert.That(icon.IconKey).IsEqualTo("build-agent");
		await Assert.That(icon.IconId).IsEqualTo(iconId);
	}

	/// <summary>
	/// The records already in the owner's browser: no header, written at the old schema. Their versions are unknown, so a log that
	/// keeps schema sets refuses them by name, never guesses (the log is then dropped and fetched again).
	/// </summary>
	[Test]
	public async Task Should_refuse_a_record_written_before_schema_sets_by_name()
	{
		var key = await PlantLegacyAsync(_store, _stream, IconAdded(new TestEvolvedComponent { Glyph = "🔥" }));
		var current = new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))));

		var failure = await Assert.ThrowsAsync<UnreadableRecordException>(async () => await ReadAllAsync(current));

		await Assert.That(failure!.Key).IsEqualTo(key.ToString());
		await Assert.That(failure.Schemas).IsNull();
		await Assert.That(failure.InnerException).IsTypeOf<InvalidOperationException>();
	}

	/// <summary>Before the header, the same record read at this build's latest schema ran past its end: the prod failure itself.</summary>
	[Test]
	public async Task Should_reproduce_the_prod_failure_when_a_pre_header_record_is_read_at_the_latest_schema()
	{
		await PlantLegacyAsync(_store, _stream, IconAdded(new TestEvolvedComponent { Glyph = "🔥" }));
		var current = new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))));

		var unreadable = new List<UnreadableRecordException>();
		await foreach (var _ in current.SalvageAsync(unreadable.Add))
		{
		}

		await Assert.That(unreadable.Single().InnerException).IsTypeOf<IndexOutOfRangeException>();
	}

	[Test]
	public async Task Should_store_a_build_s_schema_set_once_and_add_six_bytes_per_record()
	{
		var current = new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))));
		var first = IconAdded(new TestEvolvedComponent { Glyph = "a" });
		await current.AppendAsync(first);
		await current.AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "b" }));
		await new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent)))).AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "c" }));

		var sets = new List<(Guid Stream, Guid Sequence)>();
		var records = new List<byte[]>();
		await foreach (var key in _store.EnumerateKeysAsync())
		{
			if (key.Stream == Guid.Empty)
			{
				sets.Add(key);
			}
			else
			{
				records.Add(await _store.ReadBlobAsync(key));
			}
		}
		var bare = new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))).CreateSerializer().SerializeFrame<Event>(first, new byte[1024]); // stamped as stored

		await Assert.That(sets.Count).IsEqualTo(1).Because("one build, one set, whichever storage instance wrote");
		await Assert.That(records.Count).IsEqualTo(3);
		await Assert.That(records[0].Length - bare.Count).IsEqualTo(6);
	}

	[Test]
	public async Task Should_fail_loudly_on_a_schema_set_that_does_not_match_its_id()
	{
		var current = new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))));
		await current.AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "a" }));
		(Guid Stream, Guid Sequence) setKey = default;
		await foreach (var key in _store.EnumerateKeysAsync())
		{
			if (key.Stream == Guid.Empty)
			{
				setKey = key;
			}
		}
		var tampered = await _store.ReadBlobAsync(setKey);
		tampered[^1] ^= 0xFF;
		await _store.DeleteBlobAsync(setKey);
		await _store.WriteBlobAsync(setKey, tampered);

		var failure = await Assert.ThrowsAsync<UnreadableRecordException>(async () => await ReadAllAsync(new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))))));

		await Assert.That(failure!.Schemas).Contains("schema set");
	}

	[Test]
	public async Task Should_salvage_every_record_it_can_read()
	{
		var current = new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))));
		await current.AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "before" }));
		await PlantLegacyAsync(_store, _stream, IconAdded(new TestEvolvedComponent { Glyph = "unreadable" }));
		await current.AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "after" }));
		var fresh = new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))));

		var unreadable = new List<UnreadableRecordException>();
		var glyphs = new List<string?>();
		await foreach (var ev in fresh.SalvageAsync(unreadable.Add))
		{
			glyphs.Add(((TestEvolvedComponent)((ComponentAddedEvent)ev).Data!).Glyph);
		}

		await Assert.That(glyphs).IsEquivalentTo(new[] { "before", "after" });
		await Assert.That(unreadable.Count).IsEqualTo(1);
	}

	async Task<List<Event>> ReadAllAsync(BlobEventStorage storage)
	{
		var read = new List<Event>();
		await foreach (var ev in storage.GetAllAsync())
		{
			read.Add(ev);
		}
		return read;
	}

	ComponentAddedEvent IconAdded(TestEvolvedComponent icon)
	{
		var node = Guid.NewGuid();
		return new ComponentAddedEvent
		{
			EventId = Guid.CreateVersion7(),
			CommandId = Guid.NewGuid(),
			StreamId = _stream,
			TargetTypeId = Guid.NewGuid(),
			CollectionId = Guid.NewGuid(),
			TargetId = node,
			ComponentTypeId = new Guid("C0DE0000-0000-8000-9046-000000000000"),
			ComponentId = node,
			Data = icon,
		};
	}

	/// <summary>Stores <paramref name="ev"/> as a build before the header did: the bare payload, its component at schema 2026.500.</summary>
	internal async Task<(Guid Stream, Guid Sequence)> PlantLegacyAsync(SharedBrowserLogTests.SharedTailStore store, Guid stream, Event ev)
	{
		var legacy = new MappedSerializers(ser => ser.Map(MapId, 2026.500, typeof(TestEvolvedComponent))).CreateSerializer();
		var frame = legacy.SerializeFrame<Event>(ev, new byte[1024]);
		var tail = await store.ReadTailAsync();
		var key = (stream, Guid.CreateVersion7());
		await store.AppendAtTailAsync(key, frame.ToArray(), null, ev.EventId.ToString("N"), tail);
		return key;
	}

	/// <summary>A build's serializers: the built-in mappings plus <paramref name="map"/>.</summary>
	internal sealed class MappedSerializers(Action<SbxSerializer> map) : ISbxSerializerFactory
	{
		public ISbxSerializer CreateSerializer()
		{
			var serializer = new SbxSerializer();
			map(serializer);
			serializer.Snapshot();
			return serializer;
		}
	}
}
