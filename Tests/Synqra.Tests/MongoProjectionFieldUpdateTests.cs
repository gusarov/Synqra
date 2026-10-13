using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Synqra.AppendStorage;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;
using Synqra.Projection.MongoDb;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Cobra;

/// <summary>
/// A property change on the Mongo projection is a field-level <c>$set</c> on the existing document
/// (see docs/model.md §9) — never a whole-document <c>ReplaceOne</c>, and never dependent on the
/// instance being tracked in this process. Raw documents are inspected directly so the assertions are
/// about bytes on disk, not about what a deserializer makes of them.
/// </summary>
[NotInParallel]
public sealed class MongoProjectionFieldUpdateTests : BaseTest
{
	IDisposable? _streamScope;
	readonly Guid _streamId = Guid.NewGuid();

	protected override void Register(IHostApplicationBuilder hostBuilder)
	{
		base.Register(hostBuilder);
		hostBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default);
		hostBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions);
		hostBuilder.Services.AddTypeMetadataProvider(
		[
			typeof(Command),
			typeof(ChangeObjectPropertyCommand),
			typeof(AddComponentCommand),
			typeof(ChangeComponentPropertyCommand),
			typeof(DemoModel),
			typeof(TestFieldUpdateModel),
			typeof(TestGeneratedContainerNode),
			typeof(TestUniqueComponent),
		]);
		hostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Map(220, typeof(TestGeneratedContainerNode));
			ser.Map(221, typeof(TestUniqueComponent));
			ser.Map(230, typeof(TestFieldUpdateModel));
		});
		hostBuilder.Services.AddAppendStorageMongoDb<Event>(_connectionString);
		hostBuilder.Services.AddMongoDbSynqraStore(_connectionString);
	}

	// Entered inline from the accessor, not a [Before(Test)] hook — see Cobra_SynqraStoreContractTests.ResolveStore.
	IObjectStore Store
	{
		get
		{
			_streamScope ??= SynqraStreamContext.Enter(_streamId);
			return ServiceProvider.GetRequiredService<IObjectStore>();
		}
	}

	// Same database the store under test opened (AddMongoDbSynqraStore's own name resolution).
	IMongoDatabase Database()
	{
		var url = new MongoUrl(_connectionString);
		return new MongoClient(url).GetDatabase(string.IsNullOrWhiteSpace(url.DatabaseName) ? "synqra" : url.DatabaseName);
	}

	BsonDocument RootDocument<T>(Guid id)
		=> Database().GetCollection<BsonDocument>(typeof(T).Name).Find(Builders<BsonDocument>.Filter.Eq("_id", id)).Single();

	BsonDocument ComponentDocument<T>(Guid containerId)
		=> Database().GetCollection<BsonDocument>("Components").Find(Builders<BsonDocument>.Filter.And(
			Builders<BsonDocument>.Filter.Eq("_eid", containerId),
			Builders<BsonDocument>.Filter.Eq("_t", typeof(T).Name))).Single();

	/// <summary>Everything but one field, as raw BSON bytes — the "nothing else moved" oracle.</summary>
	byte[] BytesWithout(BsonDocument doc, string field)
	{
		var clone = (BsonDocument)doc.DeepClone();
		clone.Remove(field);
		return clone.ToBson();
	}

	/// <summary>
	/// A second projection over the same database that has never seen the object: its tracking cache is
	/// empty, so any write it makes must be addressed purely by id + stream.
	/// </summary>
	MongoProjection UntrackedProjection()
		=> ActivatorUtilities.CreateInstance<MongoProjection>(ServiceProvider, Database());

	ChangeComponentPropertyCommand RootChange<T>(Guid id, string propertyName, object? newValue)
	{
		var metadata = Store.TypeMetadataProvider.GetTypeMetadata(typeof(T));
		return new ChangeComponentPropertyCommand
		{
			StreamId = _streamId,
			TargetTypeId = metadata.TypeId,
			TargetId = id,
			CollectionId = metadata.GetCollectionId(""),
			ComponentTypeId = metadata.TypeId,
			ComponentId = id,
			PropertyName = propertyName,
			NewValue = newValue,
		};
	}

	[Test]
	public async Task Should_write_only_the_changed_field_of_a_root_document()
	{
		var store = Store;
		var model = new DemoModel { Name = "before", Prprpr = "keep" };
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);
		var before = RootDocument<DemoModel>(id);

		model.Name = "after";

		var after = RootDocument<DemoModel>(id);
		await Assert.That(after["Name"].AsString).IsEqualTo("after");
		await Assert.That(BytesWithout(after, "Name")).IsEquivalentTo(BytesWithout(before, "Name"));
		await Assert.That(after["_id"]).IsEqualTo(before["_id"]);
		await Assert.That(after["_sid"]).IsEqualTo(before["_sid"]);
		await Assert.That(after["_t"]).IsEqualTo(before["_t"]);
		await Assert.That(after["Prprpr"].AsString).IsEqualTo("keep");
	}

	[Test]
	public async Task Should_update_an_untracked_root_document_by_id_and_stream()
	{
		var store = Store;
		var model = new DemoModel { Name = "local", Prprpr = "keep" };
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);

		var other = UntrackedProjection();
		await Assert.That(other.ResolveObject(id)).IsNull();

		await other.SubmitCommandAsync(RootChange<DemoModel>(id, nameof(DemoModel.Name), "remote"));

		var doc = RootDocument<DemoModel>(id);
		await Assert.That(doc["Name"].AsString).IsEqualTo("remote");
		await Assert.That(doc["Prprpr"].AsString).IsEqualTo("keep");
		await Assert.That(doc["_sid"].AsGuid).IsEqualTo(_streamId);
		await Assert.That(other.ResolveObject(id)).IsNull(); // the write did not have to materialize it
	}

	[Test]
	public async Task Should_refuse_a_property_change_for_a_document_that_does_not_exist()
	{
		_ = Store;
		var missingId = Guid.NewGuid();
		var other = UntrackedProjection();

		var ex = await Assert.ThrowsAsync(async () =>
			await other.SubmitCommandAsync(RootChange<DemoModel>(missingId, nameof(DemoModel.Name), "ghost")));

		await Assert.That(ex).IsTypeOf<InvalidOperationException>();
		var count = Database().GetCollection<BsonDocument>(nameof(DemoModel)).CountDocuments(Builders<BsonDocument>.Filter.Eq("_id", missingId));
		await Assert.That(count).IsEqualTo(0L); // no partial document minted out of a single field
	}

	[Test]
	public async Task Should_not_touch_the_same_id_in_another_stream()
	{
		var store = Store;
		var model = new DemoModel { Name = "mine" };
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);

		var other = UntrackedProjection();
		using (SynqraStreamContext.Enter(Guid.NewGuid()))
		{
			var ex = await Assert.ThrowsAsync(async () =>
				await other.SubmitCommandAsync(new ChangeComponentPropertyCommand
				{
					StreamId = SynqraStreamContext.Current,
					TargetTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(DemoModel)).TypeId,
					TargetId = id,
					CollectionId = store.TypeMetadataProvider.GetTypeMetadata(typeof(DemoModel)).GetCollectionId(""),
					ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(DemoModel)).TypeId,
					ComponentId = id,
					PropertyName = nameof(DemoModel.Name),
					NewValue = "theirs",
				}));
			await Assert.That(ex).IsTypeOf<InvalidOperationException>();
		}

		await Assert.That(RootDocument<DemoModel>(id)["Name"].AsString).IsEqualTo("mine");
	}

	[Test]
	public async Task Should_write_only_the_changed_field_of_a_facet_component_document()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);
		var id = store.GetId(node);
		node.Components.Add(new TestUniqueComponent { Subject = "first" });
		var before = ComponentDocument<TestUniqueComponent>(id);

		var attached = (TestUniqueComponent)node.Components.GetUniqueComponent(typeof(TestUniqueComponent))!;
		attached.Subject = "second";

		var after = ComponentDocument<TestUniqueComponent>(id);
		await Assert.That(after["Subject"].AsString).IsEqualTo("second");
		await Assert.That(BytesWithout(after, "Subject")).IsEquivalentTo(BytesWithout(before, "Subject"));
		await Assert.That(after["_eid"].AsGuid).IsEqualTo(id);
		await Assert.That(after["_sid"].AsGuid).IsEqualTo(_streamId);
		await Assert.That(RootDocument<TestGeneratedContainerNode>(id)["Name"].AsString).IsEqualTo("n"); // container untouched
	}

	[Test]
	public async Task Should_serialize_a_field_exactly_like_a_full_document_write()
	{
		var store = Store;
		var model = new TestFieldUpdateModel { Name = "n", Weight = 1.5, Tags = new List<string> { "a" } };
		store.GetCollection<TestFieldUpdateModel>().Add(model);
		var id = store.GetId(model);

		model.Weight = 2.5;
		model.Tags = new List<string> { "x", "y" };
		model.Name = null; // IgnoreIfNull: a full write would omit the field, so the field update drops it

		// The same final state serialized whole, the way CreateRoot would have written it.
		var expected = new TestFieldUpdateModel { Weight = 2.5, Tags = new List<string> { "x", "y" } }
			.ToBsonDocument(typeof(object), MongoEventClassMaps.ScopedOpenObjectSerializer);

		var actual = RootDocument<TestFieldUpdateModel>(id);
		actual.Remove("_id");
		actual.Remove("_sid");
		await Assert.That(actual.ToBson()).IsEquivalentTo(expected.ToBson());

		// And it reads back through the projection's own deserializer as the same values.
		var other = UntrackedProjection();
		var reloaded = other.GetCollection<TestFieldUpdateModel>().Single(m => other.GetId(m) == id);
		await Assert.That(reloaded.Name).IsNull();
		await Assert.That(reloaded.Weight).IsEqualTo(2.5);
		await Assert.That(reloaded.Tags).IsEquivalentTo(["x", "y"]);
	}

	[Test]
	public async Task Should_coerce_a_rehydrated_scalar_to_the_member_type()
	{
		var store = Store;
		var model = new TestFieldUpdateModel { Name = "n", Weight = 1.0 };
		store.GetCollection<TestFieldUpdateModel>().Add(model);
		var id = store.GetId(model);

		// An event read back from a log can carry a long where the member is a double.
		await UntrackedProjection().SubmitCommandAsync(RootChange<TestFieldUpdateModel>(id, nameof(TestFieldUpdateModel.Weight), 3L));

		var doc = RootDocument<TestFieldUpdateModel>(id);
		await Assert.That(doc["Weight"].BsonType).IsEqualTo(BsonType.Double);
		await Assert.That(doc["Weight"].AsDouble).IsEqualTo(3.0);
	}
}
