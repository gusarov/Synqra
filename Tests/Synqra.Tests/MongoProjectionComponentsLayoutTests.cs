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
			typeof(TestShapeBase),
			typeof(TestCircle),
		]);
		hostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Map(220, typeof(TestGeneratedContainerNode));
			ser.Map(221, typeof(TestUniqueComponent));
			ser.Map(231, typeof(TestShapeBase));
			ser.Map(232, typeof(TestCircle));
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
	public async Task Should_resolve_one_instance_per_id_under_concurrent_resolution()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "shared" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);
		var id = store.GetId(node);
		node.Components.Add(new TestUniqueComponent { Subject = "facet" });

		// A projection that has never seen the entity: every caller below materializes from Mongo.
		var other = ActivatorUtilities.CreateInstance<MongoProjection>(ServiceProvider, Database());
		var results = new object?[32];
		var idsSeen = new Guid[results.Length];
		var facetsSeen = new int[results.Length];
		Parallel.For(0, results.Length, i =>
		{
			using (SynqraStreamContext.Enter(_streamId))
			{
				var resolved = (TestGeneratedContainerNode)other.ResolveObject(id)!;
				results[i] = resolved;
				// Observed INSIDE the race: the instance handed back must already be usable.
				idsSeen[i] = other.GetId(resolved);
				facetsSeen[i] = resolved.Components.Count;
			}
		});

		await Assert.That(results.All(r => ReferenceEquals(r, results[0]))).IsTrue();
		await Assert.That(idsSeen.All(x => x == id)).IsTrue();
		await Assert.That(facetsSeen.All(x => x == 1)).IsTrue(); // bound and hydrated before anyone could see it, exactly once
	}

	[Test]
	public async Task Should_enumerate_a_derived_root_through_its_base_collection()
	{
		var store = Store;
		store.GetCollection<TestShapeBase>().Add(new TestCircle { Label = "c", Radius = 7 });
		store.GetCollection<TestShapeBase>().Add(new TestShapeBase { Label = "b" });

		// "_t" is the actual type; the base-typed collection must still see both.
		await Assert.That(store.GetCollection<TestShapeBase>().Count).IsEqualTo(2);
		await Assert.That(store.GetCollection<TestShapeBase>().OfType<TestCircle>().Single().Radius).IsEqualTo(7);
		await Assert.That(store.GetCollection<TestCircle>().Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_store_a_facet_under_its_own_component_id_and_restore_it_on_reload()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);
		var id = store.GetId(node);
		var facet = new TestUniqueComponent { Subject = "first" };
		node.Components.Add(facet);
		var facetId = ((IIdentifiable<Guid>)facet).Id;

		var doc = Components().Find(Builders<BsonDocument>.Filter.Eq("_id", facetId)).Single();
		await Assert.That(doc["_eid"].AsGuid).IsEqualTo(id);
		await Assert.That(doc.Contains("ComponentId")).IsFalse();

		Restart();

		// A reloaded facet keeps its id, so a later change is addressable.
		var store2 = Store;
		var reloaded = (TestGeneratedContainerNode)store2.ResolveObject(id)!;
		var reloadedFacet = (TestUniqueComponent)reloaded.Components.Single();
		await Assert.That(((IIdentifiable<Guid>)reloadedFacet).Id).IsEqualTo(facetId);
		reloadedFacet.Subject = "second";
		await Assert.That(Components().Find(Builders<BsonDocument>.Filter.Eq("_id", facetId)).Single()["Subject"].AsString).IsEqualTo("second");
	}

	[Test]
	public async Task Should_refuse_a_named_collection()
	{
		var store = Store;
		var ex = await Assert.ThrowsAsync(async () =>
		{
			store.GetCollection<DemoModel>("named");
			await Task.CompletedTask;
		});
		await Assert.That(ex).IsTypeOf<NotSupportedException>();
	}

	[Test]
	public async Task Should_have_indexes_on_the_shared_collection()
	{
		var store = Store;
		store.GetCollection<DemoModel>().Add(new DemoModel { Name = "indexed" });
		var keys = Components().Indexes.List().ToList().Select(i => i["key"].AsBsonDocument.Names.Aggregate((a, b) => a + "," + b)).ToList();
		await Assert.That(keys).Contains("_sid,_t");
		await Assert.That(keys).Contains("_sid,_eid");
	}

	[Test]
	public async Task Should_delete_the_root_and_its_facets_together()
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
		// One DeleteMany by _eid: the facet goes with its entity.
		await Assert.That(Components().CountDocuments(Builders<BsonDocument>.Filter.Eq("_eid", id))).IsEqualTo(0L);
		await Assert.That(store.GetCollection<TestGeneratedContainerNode>().Count).IsEqualTo(0);
	}
}
