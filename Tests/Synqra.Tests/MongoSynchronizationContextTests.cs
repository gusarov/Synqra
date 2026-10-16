using System.Collections.Concurrent;
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
/// A synchronous write (collection Add, a generated setter) made from code that runs on a UI-like
/// SynchronizationContext (Blazor's renderer during SSR) must not deadlock: the thread is blocked in the
/// write, so a continuation posted back to its context would never run.
/// </summary>
[NotInParallel]
public sealed class MongoSynchronizationContextTests : BaseTest
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
		hostBuilder.Services.AddTestMongoDatabase(_connectionString).AddMongoEventStorage().AddServerEventLog();
		hostBuilder.Services.AddTestMongoDatabase(_connectionString).AddMongoProjection();
	}

	[Test]
	public async Task Should_complete_a_synchronous_add_made_under_a_blocked_ui_context()
	{
		var store = ServiceProvider.GetRequiredService<IObjectStore>();
		var streamId = Guid.NewGuid();
		var context = new BlockedUiContext();

		var write = Task.Run(() =>
		{
			using var _ = SynqraStreamContext.Enter(streamId);
			SynchronizationContext.SetSynchronizationContext(context);
			store.GetCollection<TestGeneratedContainerNode>().Add(new TestGeneratedContainerNode { Name = "written under a UI context" });
		});

		var finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(20))) == write;

		await Assert.That(finished).IsTrue();
		await write;
		await Assert.That(context.Posted).IsEqualTo(0); // nothing waited for the blocked context
	}

	/// <summary>A context whose posts are queued and never run, like a renderer thread that is blocked in a synchronous call.</summary>
	sealed class BlockedUiContext : SynchronizationContext
	{
		readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

		public int Posted => _queue.Count;

		public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

		public override void Send(SendOrPostCallback d, object? state) => d(state);

		public override SynchronizationContext CreateCopy() => this;
	}
}
