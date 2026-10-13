using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;
using Synqra.Projection.MongoDb;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Cobra;

/// <summary>
/// <see cref="MongoProjection"/> materializes each fetched document in place (no deep copy), so every
/// path that reads a document must read it exactly once — and what it hands out must still be the
/// attached, tracked instance the rest of the store expects.
/// </summary>
[NotInParallel]
public sealed class MongoProjectionMaterializationTests : BaseTest
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
			typeof(DemoModel),
			typeof(Command),
			typeof(ChangeObjectPropertyCommand),
			typeof(TestGraphNode),
			typeof(HierarchyLink),
			typeof(DependsOn),
			typeof(RelatedTo),
			typeof(WeightedLink),
		]);
		hostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Map(210, typeof(TestGraphNode));
			ser.Map(211, typeof(HierarchyLink));
			ser.Map(212, typeof(DependsOn));
			ser.Map(213, typeof(RelatedTo));
			ser.Map(214, typeof(WeightedLink));
			ser.Map(215, typeof(LinkAddedEvent));
			ser.Map(216, typeof(LinkRemovedEvent));
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

	[Test]
	public async Task Should_return_an_attached_link_from_TryGetByKey_whose_ends_resolve()
	{
		var store = Store;
		var parent = new TestGraphNode { Name = "parent" };
		var child = new TestGraphNode { Name = "child" };
		store.GetCollection<TestGraphNode>().Add(parent);
		store.GetCollection<TestGraphNode>().Add(child);
		parent.Children.Add(child); // HierarchyLink parent -> child

		var key = LinkKey.Directed(typeof(HierarchyLink), store.GetId(parent), store.GetId(child));
		var found = ((ILinkIndex)store).TryGetByKey(key, out var link);

		await Assert.That(found).IsTrue();
		var hierarchy = (HierarchyLink)link!;
		await Assert.That(((IBindableModel)hierarchy).Store).IsSameReferenceAs(store);
		await Assert.That(hierarchy.Source).IsSameReferenceAs(parent);
		await Assert.That(hierarchy.Target).IsSameReferenceAs(child);
	}

	[Test]
	public async Task Should_miss_in_TryGetByKey_when_only_the_reverse_direction_exists()
	{
		var store = Store;
		var parent = new TestGraphNode { Name = "parent" };
		var child = new TestGraphNode { Name = "child" };
		store.GetCollection<TestGraphNode>().Add(parent);
		store.GetCollection<TestGraphNode>().Add(child);
		parent.Children.Add(child);

		// Same endpoints, reversed — the candidate set contains the document but the key does not match.
		var reversed = LinkKey.Directed(typeof(HierarchyLink), store.GetId(child), store.GetId(parent));
		var found = ((ILinkIndex)store).TryGetByKey(reversed, out var link);

		await Assert.That(found).IsFalse();
		await Assert.That(link).IsNull();
	}

	[Test]
	public async Task Should_yield_the_same_tracked_instances_when_enumerating_a_collection_twice()
	{
		var store = Store;
		var collection = store.GetCollection<DemoModel>();
		collection.Add(new DemoModel { Name = "a" });
		collection.Add(new DemoModel { Name = "b" });

		var first = collection.ToArray();
		var second = collection.ToArray();

		await Assert.That(first.Length).IsEqualTo(2);
		await Assert.That(second.Length).IsEqualTo(2);
		for (var i = 0; i < first.Length; i++)
		{
			await Assert.That(second[i]).IsSameReferenceAs(first[i]);
			await Assert.That(store.GetId(second[i])).IsEqualTo(store.GetId(first[i]));
		}
	}

	[Test]
	public async Task Should_materialize_once_then_track_in_a_fresh_projection()
	{
		var store = Store;
		var collection = store.GetCollection<DemoModel>();
		collection.Add(new DemoModel { Name = "a", Prprpr = "keep" });
		collection.Add(new DemoModel { Name = "b", Prprpr = "keep" });

		// A second projection with an empty tracking cache: the first pass materializes from documents,
		// the second must hand back those very instances, with every field still populated.
		var url = new MongoUrl(_connectionString);
		var database = new MongoClient(url).GetDatabase(string.IsNullOrWhiteSpace(url.DatabaseName) ? "synqra" : url.DatabaseName);
		var other = ActivatorUtilities.CreateInstance<MongoProjection>(ServiceProvider, database);
		var otherCollection = other.GetCollection<DemoModel>();
		var first = otherCollection.OrderBy(x => x.Name).ToArray();
		var second = otherCollection.OrderBy(x => x.Name).ToArray();

		await Assert.That(first.Select(x => x.Name)).IsEquivalentTo(new[] { "a", "b" });
		await Assert.That(first.All(x => x.Prprpr == "keep")).IsTrue();
		for (var i = 0; i < first.Length; i++)
		{
			await Assert.That(second[i]).IsSameReferenceAs(first[i]);
			await Assert.That(other.ResolveObject(other.GetId(first[i]))).IsSameReferenceAs(first[i]);
		}
	}
}
