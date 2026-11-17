using Synqra.AppendStorage;
using Synqra.AppendStorage.BlobStorage;
using Synqra.BinarySerializer;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// A browser's local log outlives the build that wrote it. An SBX payload names no schema versions, and a reader configured at
/// its own latest read every record at that: Quotaly's IconComponent gained two members (2026.500 → 2026.873) and every older
/// record holding one ran past its end, an IndexOutOfRangeException that left the owner's page on "Opening your things…"
/// (2026-11-17). Now each record names, as a field beside its pure SBX bytes, the schemas bundle it was written with.
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

		var read = await ReadAllAsync(Current());

		var icon = (TestEvolvedComponent)((ComponentAddedEvent)read.Single()).Data!;
		await Assert.That(icon.Glyph).IsEqualTo("🔥");
		await Assert.That(icon.IconKey).IsNull();
		await Assert.That(icon.IconId).IsEqualTo(Guid.Empty);
	}

	[Test]
	public async Task Should_still_write_and_read_at_the_latest_schema()
	{
		var iconId = Guid.NewGuid();
		await Current().AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "g", IconKey = "build-agent", IconId = iconId }));

		var icon = (TestEvolvedComponent)((ComponentAddedEvent)(await ReadAllAsync(Current())).Single()).Data!;

		await Assert.That(icon.IconKey).IsEqualTo("build-agent");
		await Assert.That(icon.IconId).IsEqualTo(iconId);
	}

	/// <summary>
	/// The records already in the owner's browser name no bundle: their versions are unknown, so they are refused by name, never
	/// guessed at (the log is then dropped and fetched again).
	/// </summary>
	[Test]
	public async Task Should_refuse_a_record_that_names_no_schemas_bundle_by_name()
	{
		var key = await PlantLegacyAsync(_store, _stream, IconAdded(new TestEvolvedComponent { Glyph = "🔥" }));

		var failure = await Assert.ThrowsAsync<UnreadableRecordException>(async () => await ReadAllAsync(Current()));

		await Assert.That(failure!.Key).IsEqualTo(key.ToString());
		await Assert.That(failure.Schemas).IsNull();
		await Assert.That(failure.InnerException).IsTypeOf<InvalidOperationException>();
	}

	/// <summary>Read at this build's latest schema, the same record runs past its end: the prod failure itself.</summary>
	[Test]
	public async Task Should_reproduce_the_prod_failure_when_a_pre_bundle_record_is_read_at_the_latest_schema()
	{
		await PlantLegacyAsync(_store, _stream, IconAdded(new TestEvolvedComponent { Glyph = "🔥" }));

		var unreadable = new List<UnreadableRecordException>();
		await foreach (var _ in Current().SalvageAsync(unreadable.Add))
		{
		}

		await Assert.That(unreadable.Single().InnerException).IsTypeOf<IndexOutOfRangeException>();
	}

	[Test]
	public async Task Should_salvage_every_record_it_can_read()
	{
		await Current().AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "before" }));
		await PlantLegacyAsync(_store, _stream, IconAdded(new TestEvolvedComponent { Glyph = "unreadable" }));
		await Current().AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "after" }));

		var unreadable = new List<UnreadableRecordException>();
		var glyphs = new List<string?>();
		var streams = new List<Guid>();
		await foreach (var ev in Current().SalvageAsync(unreadable.Add))
		{
			glyphs.Add(((TestEvolvedComponent)((ComponentAddedEvent)ev).Data!).Glyph);
			streams.Add(ev.StreamId);
		}

		await Assert.That(glyphs).IsEquivalentTo(new[] { "before", "after" });
		await Assert.That(streams.Distinct().Single()).IsEqualTo(_stream).Because("a salvaged event goes back to its stream");
		await Assert.That(unreadable.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_store_one_bundle_per_build_and_keep_the_blob_pure_sbx()
	{
		var first = IconAdded(new TestEvolvedComponent { Glyph = "a" });
		var storage = Current();
		await storage.AppendAsync(first);
		await storage.AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "b" }));
		await Current().AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "c" }));

		var stored = await _store.ReadTaggedBlobAsync((first.StreamId, first.SequenceId));
		var bare = new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))).CreateSerializer().SerializeFrame<Event>(first, new byte[1024]);

		var bundles = _store.ListSideKeys("schemas");
		await Assert.That(bundles.Count).IsEqualTo(1).Because("one build, one bundle, whichever storage instance wrote");
		await Assert.That(_store.Count).IsEqualTo(3).Because("a bundle is a side entry, not a record");
		await Assert.That(stored.Tag!.Value.ToString("x8")).IsEqualTo(bundles.Single()).Because("a record's tag is its bundle's id");
		await Assert.That(stored.Blob.SequenceEqual(bare.ToArray())).IsTrue().Because("the bundle id is a field, not bytes in the blob");
	}

	[Test]
	public async Task Should_fail_loudly_on_a_schemas_bundle_that_does_not_match_its_id()
	{
		await Current().AppendAsync(IconAdded(new TestEvolvedComponent { Glyph = "a" }));
		var bundleId = _store.ListSideKeys("schemas").Single();
		var content = (await _store.ReadSideEntriesAsync("schemas"))[bundleId];
		content[^1] ^= 0xFF;
		_store.TamperSideEntry("schemas", bundleId, content);

		var failure = await Assert.ThrowsAsync<UnreadableRecordException>(async () => await ReadAllAsync(Current()));

		await Assert.That(failure!.Schemas).Contains("schemas bundle");
	}

	BlobEventStorage Current() => new BlobEventStorage(_store, new MappedSerializers(ser => ser.Map(MapId, typeof(TestEvolvedComponent))));

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

	/// <summary>Stores <paramref name="ev"/> as a build before bundles did: bare SBX, no bundle field, its component at schema 2026.500.</summary>
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
		// Read back through Activator only: the published (NativeAOT) run trimmed its constructor and setters otherwise.
		[System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All, typeof(TestEvolvedComponent))]
		public ISbxSerializer CreateSerializer()
		{
			var serializer = new SbxSerializer();
			map(serializer);
			serializer.Snapshot();
			return serializer;
		}
	}
}
