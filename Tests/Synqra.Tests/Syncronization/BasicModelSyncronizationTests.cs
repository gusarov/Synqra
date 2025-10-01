﻿using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.DemoTodo;
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

internal class BasicModelSyncronizationTests : BaseTest
{
	SynqraTestNode _nodeMaster;

	SynqraTestNode _nodeA;

	SynqraTestNode _nodeB;

	[Before(Test)]
	public void SetUp()
	{
		_nodeMaster = new SynqraTestNode(sp =>
		{

		}, masterHost: true);
		_nodeA = new SynqraTestNode(sp => { }) { Port = _nodeMaster.Port, };
		_nodeB = new SynqraTestNode(sp => { }) { Port = _nodeMaster.Port, };
		// Console.WriteLine("Master: "+ _nodeMaster.Host.Environment.ContentRootPath);
		// Console.WriteLine("_nodeA: " + _nodeA.Host.Environment.ContentRootPath);
		// Console.WriteLine("_nodeB: " + _nodeB.Host.Environment.ContentRootPath);
	}

	// [Test] // actually attach is internal method!!
	public async Task Should_attach_new_object_and_it_should_persist()
	{
		// the Attach() call alone should persist new object. This allows to make snapshot of new obejct with all properties and start tracking changes
		var task = new MyTaskModel { Subject = "Task 1" };
		var data = _nodeA.StoreContext.Attach(task, null);
		Assert.Fail("Synchronization failed");
	}

	[Test]
	public async Task Should_have_node_with_model()
	{
		var collection = _nodeA.StoreContext.GetCollection<MyTaskModel>();
		await Assert.That(collection).IsEmpty();
		var task = new MyTaskModel { Subject = "Task 1" };
		collection.Add(task);
		await Assert.That(collection).HasCount(1);
		task.Subject = "Task 1 - updated";
		await Assert.That(task.Subject).IsEqualTo("Task 1 - updated");
	}

	// [Test] // in progress
	public async Task Should_syncronize_simple_models()
	{
		await Should_have_node_with_model(); // Works on Node A
		var collection = _nodeB.StoreContext.GetCollection<MyTaskModel>(); //Same happened with Node B!!
		var sw = Stopwatch.StartNew();
		while (collection.Count < 2 && sw.ElapsedMilliseconds < 2000)
		{
			await Task.Delay(100); // wait until all commands are processed
		}
		await Assert.That(collection).HasCount(1);
		var task = collection.First();
		await Assert.That(task.Subject).IsEqualTo("Task 1 - updated");
	}
}
