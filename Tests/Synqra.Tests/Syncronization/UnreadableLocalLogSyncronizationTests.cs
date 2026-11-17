using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.AppendStorage.BlobStorage;
using Synqra.BlobStorage;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System.Diagnostics;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// The local log is a cache of the server's stream: a record in it this build cannot read must never keep the page from opening.
/// The client drops the log, keeps what it wrote and the master has not acknowledged, and fetches the stream again (2026-11-17,
/// the owner's quotaly.com stuck on "Opening your things…" over an IconComponent record of an older schema).
/// </summary>
internal class UnreadableLocalLogSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;

	[Test]
	public async Task A_client_whose_local_log_holds_an_unreadable_record_opens_resyncs_and_keeps_its_unsent_write()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await nodeMaster.Started;
		var nodeB = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeB.Started;
		await WaitForOnlineAsync(nodeB);
		nodeB.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "on the server" });
		await WaitUntilAsync(() => Task.FromResult(HasTask(nodeMaster, "on the server")), "B's write reaches the master");

		// The previous page load: offline, it wrote a task the master never saw, then its build's records went unreadable.
		var store = new SharedBrowserLogTests.SharedTailStore();
		nodeMaster.RefusesConnections = true;
		var previous = new SynqraTestNode(streamId, builder => UseLog(builder, store)) { Port = nodeMaster.Port };
		await previous.Started;
		previous.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "written offline" });
		await WaitUntilAsync(() => Task.FromResult(store.Count > 0), "the offline write is in the local log");
		await previous.Host.StopAsync();
		await new LocalLogSchemaTests().PlantLegacyAsync(store, streamId, new ObjectDeletedEvent { EventId = Guid.CreateVersion7(), CommandId = Guid.NewGuid(), StreamId = streamId, TargetId = Guid.NewGuid(), TargetTypeId = Guid.NewGuid(), CollectionId = Guid.NewGuid() });
		var garbage = await store.ReadTailAsync();
		await store.AppendAtTailAsync((streamId, Guid.CreateVersion7()), new byte[] { 0x6D, 0xFF, 0xFF }, null, Guid.NewGuid().ToString("N"), garbage);
		nodeMaster.RefusesConnections = false;

		var nodeA = new SynqraTestNode(streamId, builder => UseLog(builder, store)) { Port = nodeMaster.Port };
		await nodeA.Started; // the page opens: this threw IndexOutOfRangeException out of the cold fold

		await WaitUntilAsync(() => Task.FromResult(HasTask(nodeA, "on the server")), "A resyncs the server's stream");
		await WaitUntilAsync(() => Task.FromResult(HasTask(nodeMaster, "written offline")), "A's unacknowledged write reaches the master");
	}

	void UseLog(IHostApplicationBuilder builder, SharedBrowserLogTests.SharedTailStore store)
	{
		builder.Services.AddSingleton<IBlobStorage<(Guid Stream, Guid Sequence)>>(store);
		builder.Services.Replace(ServiceDescriptor.Singleton<IAppendStorage<Event, (Guid Stream, Guid Sequence)>, BlobEventStorage>());
	}

	bool HasTask(SynqraTestNode node, string subject) => node.StoreContext.GetCollection<SampleTaskModel>().Any(x => x.Subject == subject);

	async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
	{
		var sw = Stopwatch.StartNew();
		while (!await condition() && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		await Assert.That(await condition()).IsTrue().Because($"timed out waiting until {what}");
	}

	async Task WaitForOnlineAsync(SynqraTestNode node)
	{
		var sw = Stopwatch.StartNew();
		while (!node.StoreContext.IsOnline())
		{
			if (sw.ElapsedMilliseconds > PropagationTimeoutMs && !Debugger.IsAttached)
			{
				throw new TimeoutException("Node did not come online within 30s");
			}
			await Task.Delay(100);
		}
	}
}
