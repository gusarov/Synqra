using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;
using Synqra.Projection.MongoDb;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// The master's seed through the production store: the multitenant <see cref="MongoProjection"/> over the Mongo event log. The
/// seed lands in its own stream only, once, and never in a stream that holds history (docs/model.md §10).
/// </summary>
[NotInParallel]
public sealed class MongoStreamSeedingTests : BaseTest
{
	const string SeedName = "seed";

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
		hostBuilder.Services.AddTestMongoDatabase(_connectionString).AddMongoEventStorage().AddServerEventLog();
		hostBuilder.Services.AddTestMongoDatabase(_connectionString).AddMongoProjection();
		hostBuilder.Services.AddStreamSeeder<NodeSeeder>();
	}

	IObjectStore Store => ServiceProvider.GetRequiredService<IObjectStore>();

	IEventLog Log => ServiceProvider.GetRequiredService<IEventLog>();

	StreamSeeding Seeding => ServiceProvider.GetRequiredService<StreamSeeding>();

	int CountSeedsIn(Guid stream)
	{
		using (SynqraStreamContext.Enter(stream))
		{
			return Store.GetCollection<TestGeneratedContainerNode>().Count(n => n.Name == SeedName);
		}
	}

	async Task<int> CountEventsAsync(Guid stream)
	{
		var count = 0;
		await foreach (var _ in Log.ReadStreamAsync(stream, default))
		{
			count++;
		}
		return count;
	}

	[Test]
	public async Task Should_seed_an_empty_stream_into_that_stream_only()
	{
		var stream = Guid.NewGuid();
		var neighbour = Guid.NewGuid();

		var seeded = await Seeding.SeedIfEmptyAsync(stream, Log, Store);

		await Assert.That(seeded).IsTrue();
		await Assert.That(CountSeedsIn(stream)).IsEqualTo(1);
		await Assert.That(CountSeedsIn(neighbour)).IsEqualTo(0);
		await Assert.That(await CountEventsAsync(stream)).IsGreaterThan(0);
		await Assert.That(await CountEventsAsync(neighbour)).IsEqualTo(0);
	}

	[Test]
	public async Task Should_not_seed_a_stream_that_holds_history()
	{
		var stream = Guid.NewGuid();
		using (SynqraStreamContext.Enter(stream))
		{
			Store.GetCollection<TestGeneratedContainerNode>().Add(new TestGeneratedContainerNode { Name = "history" });
		}

		var seeded = await Seeding.SeedIfEmptyAsync(stream, Log, Store);

		await Assert.That(seeded).IsFalse();
		await Assert.That(CountSeedsIn(stream)).IsEqualTo(0);
	}

	[Test]
	public async Task Should_not_seed_again_after_a_restart_over_the_same_database()
	{
		var stream = Guid.NewGuid();
		await Seeding.SeedIfEmptyAsync(stream, Log, Store);

		var restarted = new StreamSeeding([new NodeSeeder()]); // nothing in memory: the Mongo log alone answers
		var seeded = await restarted.SeedIfEmptyAsync(stream, Log, Store);

		await Assert.That(seeded).IsFalse();
		await Assert.That(CountSeedsIn(stream)).IsEqualTo(1);
	}

	[Test]
	public async Task Should_seed_once_when_subscribers_race()
	{
		var stream = Guid.NewGuid();
		var (seeding, log, store) = (Seeding, Log, Store); // the test host builds lazily, and not for racing callers
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var racers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
		{
			await start.Task;
			return await seeding.SeedIfEmptyAsync(stream, log, store);
		})).ToArray();
		start.SetResult();

		var results = await Task.WhenAll(racers);

		await Assert.That(results.Count(r => r)).IsEqualTo(1);
		await Assert.That(CountSeedsIn(stream)).IsEqualTo(1);
	}

	/// <summary>One node per seeded stream, written in the stream <see cref="StreamSeeding"/> puts in scope.</summary>
	public sealed class NodeSeeder : IStreamSeeder
	{
		public Task SeedAsync(StreamSeedContext context, IObjectStore store)
		{
			store.GetCollection<TestGeneratedContainerNode>().Add(new TestGeneratedContainerNode { Name = SeedName });
			return Task.CompletedTask;
		}
	}
}
