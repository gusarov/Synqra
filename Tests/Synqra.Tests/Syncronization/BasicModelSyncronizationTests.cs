using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Synqra.Tests.Syncronization;

[NotInParallel]
internal class BasicModelSyncronizationTests : BaseTest
{
	SynqraTestNode _nodeMaster;

	SynqraTestNode _nodeA;

	SynqraTestNode _nodeB;

	[Before(Test)]
	public async Task SetUp()
	{
		var streamId = Guid.NewGuid();
		_nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await _nodeMaster.Started; // master Port is the OS-assigned one, known only after bind
		_nodeA = new SynqraTestNode(streamId, builder => { }) { Port = _nodeMaster.Port, };
		_nodeB = new SynqraTestNode(streamId, builder => { }) { Port = _nodeMaster.Port, };
		await _nodeA.Started;
		await _nodeB.Started;
		// Console.WriteLine("Master: "+ _nodeMaster.Host.Environment.ContentRootPath);
		// Console.WriteLine("_nodeA: " + _nodeA.Host.Environment.ContentRootPath);
		// Console.WriteLine("_nodeB: " + _nodeB.Host.Environment.ContentRootPath);
	}

	// [Test] // actually attach is internal method!!
	public async Task Should_05_attach_new_object_and_it_should_persist()
	{
		// the Attach() call alone should persist new object. This allows to make snapshot of new obejct with all properties and start tracking changes
		var task = new SampleTaskModel { Subject = "Task 1" };
		var data = _nodeA.StoreContext.Attach(task, null);
		Assert.Fail("Synchronization failed");
	}

	[Test]
	public async Task Should_10_have_node_with_model()
	{
		var collection = _nodeA.StoreContext.GetCollection<SampleTaskModel>();
		await Assert.That(collection).IsEmpty();
		var task = new SampleTaskModel { Subject = "Task 1" };
		collection.Add(task);
		await Assert.That(collection).HasCount(1);
		task.Subject = "Task 1 - updated";
		await Assert.That(task.Subject).IsEqualTo("Task 1 - updated");
	}

	[Test]
	public async Task Should_15_have_node_with_model()
	{
		var collection = _nodeA.StoreContext.GetCollection<SampleTaskModel>();
		await Assert.That(collection).IsEmpty();
		var task = new SampleTaskModel { Subject = "Task 1" };
		collection.Add(task);
		await Assert.That(collection).HasCount(1);
		for (int i = 0; i < 50; i++)
		{
			task.Subject = "Task 1 - updated " + i;
			await Assert.That(task.Subject).IsEqualTo("Task 1 - updated " + i);
		}
	}

	[Test]
	public async Task Should_20_synchronize_simple_models()
	{
		await WaitForOnlineAsync();
		EmergencyLog.Default.Message("°0 <==========> Should_20_synchronize_simple_models");
		await Should_10_have_node_with_model(); // Works on Node A
		var collection = _nodeB.StoreContext.GetCollection<SampleTaskModel>(); //Same happened with Node B!!
		var sw = Stopwatch.StartNew();
		while (collection.Count < 1 && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100); // wait until all commands are processed
		}
		await Assert.That(collection).HasCount(1);
		var task = collection.First();
		sw = Stopwatch.StartNew();
		while (task.Subject != "Task 1 - updated" && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100); // wait until all commands are processed
		}
		await Assert.That(task.Subject).IsEqualTo("Task 1 - updated");
		EmergencyLog.Default.Message("°0 </==========> Should_20_synchronize_simple_models");
	}

	[Test]
	public async Task Should_30_synchronize_update_made_after_initial_sync()
	{
		await WaitForOnlineAsync();
		var collectionA = _nodeA.StoreContext.GetCollection<SampleTaskModel>();
		var taskA = new SampleTaskModel { Subject = "Task 1" };
		collectionA.Add(taskA);

		// Let the create propagate to node B before updating, so node A's replication
		// sender provably drained its event log to the end in between. This pins the
		// regression where an exhausted log enumerator never resumed, silently dropping
		// every event appended after the drain.
		var collectionB = _nodeB.StoreContext.GetCollection<SampleTaskModel>();
		var sw = Stopwatch.StartNew();
		while (collectionB.Count < 1 && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		await Assert.That(collectionB).HasCount(1);

		taskA.Subject = "Task 1 - updated";

		var taskB = collectionB.First();
		sw = Stopwatch.StartNew();
		while (taskB.Subject != "Task 1 - updated" && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		await Assert.That(taskB.Subject).IsEqualTo("Task 1 - updated");
	}

	// Matches the 30s online-wait budget below: CI runs the net8 and net10 targets in parallel
	// inside docker, each spinning up several Kestrel+WebSocket hosts, so under CPU contention a
	// node can take most of that budget just to come online — leaving a tighter 10s propagation
	// window that occasionally lapses (a peer at count 0), even though the events never drop. The
	// polling loops exit the instant the condition is met, so the happy path never waits the full
	// budget; this only widens the ceiling before a genuinely stuck sync is declared a failure.
	const int PropagationTimeoutMs = 30_000;

	async Task WaitForOnlineAsync()
	{
		var sw = Stopwatch.StartNew();
		while (!(_nodeA.StoreContext.IsOnline() && _nodeB.StoreContext.IsOnline()))
		{
			if (sw.ElapsedMilliseconds > 30_000 && !Debugger.IsAttached)
			{
				throw new TimeoutException("Nodes did not come online within 30s");
			}
			await Task.Delay(100);
		}
	}
}
