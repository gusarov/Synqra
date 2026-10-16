using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;
using Synqra.Projection.MongoDb;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Cobra;

/// <summary>
/// <see cref="MongoProjection.MongoProjection"/> reads <see cref="SynqraStreamContext.Current"/> on
/// every call rather than capturing a stream at construction — it is a process-wide singleton, so
/// the SAME instance must serve whatever stream the caller's ambient context says, without leaking
/// one stream's documents into another's queries. Every Mongo query/write is scoped by a "_sid"
/// field stamped on every document (objects, components, and links alike) — the property the
/// "Todo" prior art enforced via its own ContainerId column, applied here to Synqra's stream
/// concept via <see cref="SynqraStreamContext"/> instead of a per-call parameter.
/// </summary>
[NotInParallel]
public sealed class MongoProjectionStreamIsolationTests : BaseTest
{
	protected override void Register(Microsoft.Extensions.Hosting.IHostApplicationBuilder hostBuilder)
	{
		base.Register(hostBuilder);
		hostBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default);
		hostBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions);
		hostBuilder.Services.AddTypeMetadataProvider([typeof(TestGeneratedContainerNode), typeof(TestUniqueComponent)]);
		hostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(220, typeof(TestGeneratedContainerNode));
			ser.Map(221, typeof(TestUniqueComponent));
		});
		hostBuilder.Services.AddMongoEventLog(_connectionString);
		hostBuilder.Services.AddMongoDbSynqraStore(_connectionString);
	}

	IObjectStore Store => ServiceProvider.GetRequiredService<IObjectStore>();

	[Test]
	public async Task Should_not_see_another_streams_objects_in_the_same_database()
	{
		var store = Store;
		var streamA = Guid.NewGuid();
		var streamB = Guid.NewGuid();

		using (SynqraStreamContext.Enter(streamA))
		{
			store.GetCollection<TestGeneratedContainerNode>().Add(new TestGeneratedContainerNode { Name = "from-a" });
		}
		using (SynqraStreamContext.Enter(streamB))
		{
			store.GetCollection<TestGeneratedContainerNode>().Add(new TestGeneratedContainerNode { Name = "from-b" });
		}

		List<string?> seenByA;
		using (SynqraStreamContext.Enter(streamA))
		{
			seenByA = store.GetCollection<TestGeneratedContainerNode>().Select(x => x.Name).ToList();
		}
		List<string?> seenByB;
		using (SynqraStreamContext.Enter(streamB))
		{
			seenByB = store.GetCollection<TestGeneratedContainerNode>().Select(x => x.Name).ToList();
		}

		await Assert.That(seenByA).IsEquivalentTo(["from-a"]);
		await Assert.That(seenByB).IsEquivalentTo(["from-b"]);
	}

	[Test]
	public async Task Should_not_see_another_streams_components_in_the_same_database()
	{
		var store = Store;
		var streamA = Guid.NewGuid();
		var streamB = Guid.NewGuid();

		TestGeneratedContainerNode nodeA;
		using (SynqraStreamContext.Enter(streamA))
		{
			nodeA = new TestGeneratedContainerNode { Name = "n-a" };
			store.GetCollection<TestGeneratedContainerNode>().Add(nodeA);
			nodeA.Components.Add(new TestUniqueComponent { Subject = "a-component" });
		}

		TestGeneratedContainerNode nodeB;
		using (SynqraStreamContext.Enter(streamB))
		{
			nodeB = new TestGeneratedContainerNode { Name = "n-b" };
			store.GetCollection<TestGeneratedContainerNode>().Add(nodeB);
			nodeB.Components.Add(new TestUniqueComponent { Subject = "b-component" });
		}

		await Assert.That(nodeA.Components.Count).IsEqualTo(1);
		await Assert.That(nodeB.Components.Count).IsEqualTo(1);
		await Assert.That(((TestUniqueComponent)nodeA.Components.GetUniqueComponent(typeof(TestUniqueComponent))!).Subject).IsEqualTo("a-component");
		await Assert.That(((TestUniqueComponent)nodeB.Components.GetUniqueComponent(typeof(TestUniqueComponent))!).Subject).IsEqualTo("b-component");
	}

	[Test]
	public async Task Should_not_resolve_another_streams_tracked_object_by_id()
	{
		var store = Store;
		var streamA = Guid.NewGuid();
		var streamB = Guid.NewGuid();

		// Held on purpose: the instance stays tracked, which is exactly when a stream-blind identity map would hand it out.
		var nodeA = new TestGeneratedContainerNode { Name = "secret-a" };
		Guid id;
		using (SynqraStreamContext.Enter(streamA))
		{
			store.GetCollection<TestGeneratedContainerNode>().Add(nodeA);
			id = store.GetId(nodeA);
			await Assert.That(store.ResolveObject(id)).IsSameReferenceAs(nodeA);
		}

		object? seenByB;
		Guid versionSeenByB;
		using (SynqraStreamContext.Enter(streamB))
		{
			seenByB = store.ResolveObject(id);
			versionSeenByB = store.GetLastEventId(id);
		}

		await Assert.That(seenByB).IsNull();
		await Assert.That(versionSeenByB).IsEqualTo(Guid.Empty);
		using (SynqraStreamContext.Enter(streamA))
		{
			await Assert.That(store.ResolveObject(id)).IsSameReferenceAs(nodeA);
		}
	}
}
