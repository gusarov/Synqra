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
/// Physical layout of the Mongo projection (docs/model.md §9): one shared <c>Components</c> collection,
/// roots recognised by <c>_id == _eid</c>, no per-type collections.
/// </summary>
[NotInParallel]
public sealed class MongoProjectionComponentsLayoutTests : BaseTest
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
			typeof(AddComponentCommand),
			typeof(ChangeComponentPropertyCommand),
			typeof(DeleteComponentCommand),
			typeof(DemoModel),
			typeof(TestGeneratedContainerNode),
			typeof(TestUniqueComponent),
		]);
		hostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Map(220, typeof(TestGeneratedContainerNode));
			ser.Map(221, typeof(TestUniqueComponent));
		});
		hostBuilder.Services.AddAppendStorageMongoDb<Event>(_connectionString);
		hostBuilder.Services.AddMongoDbSynqraStore(_connectionString);
	}

	IObjectStore Store
	{
		get
		{
			_streamScope ??= SynqraStreamContext.Enter(_streamId);
			return ServiceProvider.GetRequiredService<IObjectStore>();
		}
	}

	IMongoDatabase Database()
	{
		var url = new MongoUrl(_connectionString);
		return new MongoClient(url).GetDatabase(string.IsNullOrWhiteSpace(url.DatabaseName) ? "synqra" : url.DatabaseName);
	}

	IMongoCollection<BsonDocument> Components() => Database().GetCollection<BsonDocument>("Components");

	[Test]
	public async Task Should_store_a_root_as_a_self_owned_document_in_the_shared_collection()
	{
		var store = Store;
		var model = new DemoModel { Name = "root" };
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);

		var doc = Components().Find(Builders<BsonDocument>.Filter.Eq("_id", id)).Single();
		await Assert.That(doc["_eid"].AsGuid).IsEqualTo(id);
		await Assert.That(doc["_t"].AsString).IsEqualTo(nameof(DemoModel));
		await Assert.That(doc["_sid"].AsGuid).IsEqualTo(_streamId);
		await Assert.That(doc["Name"].AsString).IsEqualTo("root");

		var collectionNames = Database().ListCollectionNames().ToList();
		await Assert.That(collectionNames).DoesNotContain(nameof(DemoModel));
	}

	[Test]
	public async Task Should_enumerate_only_this_types_roots_not_facets_or_other_types()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);
		node.Components.Add(new TestUniqueComponent { Subject = "facet" });
		store.GetCollection<DemoModel>().Add(new DemoModel { Name = "other" });

		await Assert.That(store.GetCollection<TestGeneratedContainerNode>().Count).IsEqualTo(1);
		await Assert.That(store.GetCollection<TestGeneratedContainerNode>().Select(n => n.Name).ToList()).IsEquivalentTo(["n"]);
		await Assert.That(store.GetCollection<DemoModel>().Count).IsEqualTo(1);
		await Assert.That(Components().CountDocuments(Builders<BsonDocument>.Filter.Eq("_sid", _streamId))).IsEqualTo(3L);
	}

	[Test]
	public async Task Should_not_reattach_a_container_as_its_own_facet_on_reload()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);
		var id = store.GetId(node);
		node.Components.Add(new TestUniqueComponent { Subject = "only" });

		Restart();

		var store2 = Store;
		var reloaded = store2.GetCollection<TestGeneratedContainerNode>().Single(n => store2.GetId(n) == id);
		await Assert.That(reloaded.Components.Count).IsEqualTo(1);
		await Assert.That(reloaded.Components.Single()).IsTypeOf<TestUniqueComponent>();
	}

	[Test]
	public async Task Should_delete_only_the_roots_own_document()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);
		var id = store.GetId(node);
		node.Components.Add(new TestUniqueComponent { Subject = "facet" });
		var metadata = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestGeneratedContainerNode));

		await store.SubmitCommandAsync(new DeleteComponentCommand
		{
			StreamId = _streamId,
			TargetTypeId = metadata.TypeId,
			TargetId = id,
			CollectionId = metadata.GetCollectionId(""),
			ComponentTypeId = metadata.TypeId,
			ComponentId = id,
		});

		await Assert.That(Components().CountDocuments(Builders<BsonDocument>.Filter.Eq("_id", id))).IsEqualTo(0L);
		// Facet cascade is a later phase — the facet document is still there, addressed by its owner.
		await Assert.That(Components().CountDocuments(Builders<BsonDocument>.Filter.Eq("_eid", id))).IsEqualTo(1L);
		await Assert.That(store.GetCollection<TestGeneratedContainerNode>().Count).IsEqualTo(0);
	}
}
