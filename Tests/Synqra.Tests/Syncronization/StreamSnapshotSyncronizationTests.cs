using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Synqra.BinarySerializer;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Syncronization;

#if NET10_0_OR_GREATER
/// <summary>
/// A cold client loads its stream's state, not its history (plans/snapshot.md), from an in-memory master and from the Mongo-backed
/// master a production host runs. Each test builds a scene on the master — roots, a unique and a repeated facet, links with a
/// payload, edits and a delete — and compares what a client holds with what the master holds, entity by entity.
/// </summary>
[NotInParallel] // the Mongo-backed masters share one mongod
internal class StreamSnapshotSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;
	const int SceneNodes = 40;

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task A_cold_client_loads_the_state_and_holds_what_a_replaying_client_holds(bool mongo)
	{
		var streamId = Guid.NewGuid();
		var master = await StartMasterAsync(streamId, mongo);
		await BuildSceneAsync(master, SceneNodes);
		var expected = await DescribeAsync(master);
		var events = await CountEventsAsync(master);

		var loaded = await StartClientAsync(master, streamId);
		var replayed = await StartClientAsync(master, streamId, requestSnapshot: false);

		await ExpectSameSceneAsync(loaded, master, "the snapshot-loaded client holds the master's scene");
		await ExpectSameSceneAsync(replayed, master, "the replaying client holds the master's scene");
		await Assert.That(Replication(loaded).SnapshotsLoaded).IsEqualTo(1);
		await Assert.That(Replication(loaded).FramesReceived).IsEqualTo(0).Because("nothing was written after the snapshot's watermark");
		await Assert.That(Replication(replayed).SnapshotsLoaded).IsEqualTo(0);
		await Assert.That(Replication(replayed).FramesReceived).IsEqualTo(events);
		await Assert.That(await CountEventsAsync(loaded)).IsEqualTo(1).Because("the client's log holds the snapshot record alone");
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task A_snapshot_loaded_client_follows_writes_both_ways(bool mongo)
	{
		var streamId = Guid.NewGuid();
		var master = await StartMasterAsync(streamId, mongo);
		var ids = await BuildSceneAsync(master, SceneNodes);
		var client = await StartClientAsync(master, streamId);
		await ExpectSameSceneAsync(client, master, "the client holds the master's scene");

		// The master writes after the snapshot: it reaches the client as an event.
		await RunOnMasterAsync(master, store => RenameAsync(store, ids[0], "renamed on the master"));
		// The client writes on an entity it got from the snapshot: it reaches the master, and the version it had is the master's.
		await RenameAsync(client.StoreContext, ids[1], "renamed on the client");
		await AddEdgeAsync(client.StoreContext, ids[2], ids[7], 7.5); // ids[2] already links to ids[3]: the chain

		await ExpectSameSceneAsync(client, master, "both hold both writes");
		await Assert.That(await DescribeAsync(master)).Contains("renamed on the client");
		await Assert.That(await DescribeAsync(client)).Contains("renamed on the master");
		await Assert.That(Replication(client).SnapshotsLoaded).IsEqualTo(1);
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task A_warm_reload_after_a_snapshot_keeps_the_state_and_takes_only_the_delta(bool mongo)
	{
		var streamId = Guid.NewGuid();
		var master = await StartMasterAsync(streamId, mongo);
		var ids = await BuildSceneAsync(master, SceneNodes);
		var state = new RecordingStateStore();
		var client = await StartClientAsync(master, streamId, configure: builder => builder.Services.AddSingleton<IEventReplicationStateStore>(state));
		await ExpectSameSceneAsync(client, master, "the client holds the master's scene");
		await WaitUntilAsync(() => Task.FromResult(state.Saved?.ServerWatermark != default), "the client persisted the snapshot's watermark");
		var log = await EndRunAsync(client);

		await RunOnMasterAsync(master, store => RenameAsync(store, ids[5], "renamed while the client was away"));
		var delta = await CountEventsAsync(master) - await CountEventsAsync(master, before: state.Saved!.ServerWatermark);

		// The client's next run: its log (the snapshot and nothing after it) and its state.
		var next = await StartClientAsync(master, streamId, configure: builder =>
		{
			builder.Configuration[LogFileKey] = log;
			builder.Services.AddSingleton<IEventReplicationStateStore>(new RecordingStateStore(state.Saved));
		});
		await ExpectSameSceneAsync(next, master, "the reloaded client holds the scene and the write it missed");
		await Task.Delay(500); // nothing more may follow

		await Assert.That(Replication(next).SnapshotsLoaded).IsEqualTo(0).Because("a warm client resumes from its watermark");
		await Assert.That(Replication(next).FramesReceived).IsEqualTo(delta);
	}

	/// <summary>
	/// A host that keeps no replication state (Contoso's browser) asks every load for the whole stream: its log, which starts with the
	/// snapshot, names the floor it resumes after, and nothing the snapshot holds is folded in twice.
	/// </summary>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task A_warm_reload_without_a_state_store_resumes_after_the_stored_snapshot(bool mongo)
	{
		var streamId = Guid.NewGuid();
		var master = await StartMasterAsync(streamId, mongo);
		var ids = await BuildSceneAsync(master, SceneNodes);
		var client = await StartClientAsync(master, streamId);
		await ExpectSameSceneAsync(client, master, "the client holds the master's scene");
		var cutAt = await CountEventsAsync(master);
		var log = await EndRunAsync(client);

		await RunOnMasterAsync(master, store => RenameAsync(store, ids[5], "renamed while the client was away"));
		var delta = await CountEventsAsync(master) - cutAt;

		var next = await StartClientAsync(master, streamId, configure: builder => builder.Configuration[LogFileKey] = log);
		await ExpectSameSceneAsync(next, master, "the reloaded client holds the scene once, and the write it missed");
		await Task.Delay(500); // nothing more may follow

		await Assert.That(await DescribeAsync(next)).IsEqualTo(await DescribeAsync(master));
		await Assert.That(Replication(next).FramesReceived).IsEqualTo(delta).Because("the stored snapshot's watermark is where the client resumes");
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task Writes_made_while_clients_take_their_snapshots_reach_every_client(bool mongo)
	{
		var streamId = Guid.NewGuid();
		var master = await StartMasterAsync(streamId, mongo);
		var ids = await BuildSceneAsync(master, SceneNodes);

		using var stop = new CancellationTokenSource();
		var writes = 0;
		// Paced and capped: the writes have to land during the cuts, not flood the clients. Unpaced, a loaded agent made thousands
		// while four clients connected, more than a client takes in within the budget.
		var writer = Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested && writes < 400)
			{
				await RunOnMasterAsync(master, store => RenameAsync(store, ids[writes % ids.Count], $"write {writes}"));
				writes++;
				await Task.Delay(5);
			}
		});
		var clients = new List<SynqraTestNode>();
		for (var i = 0; i < 4; i++)
		{
			clients.Add(await StartClientAsync(master, streamId));
		}
		await WaitUntilAsync(() => Task.FromResult(clients.All(c => Replication(c).SnapshotsLoaded == 1)), "every client loaded a snapshot");
		stop.Cancel();
		await writer;

		var expected = await DescribeAsync(master);
		foreach (var client in clients)
		{
			var sw = Stopwatch.StartNew();
			while (await DescribeAsync(client) != expected && sw.ElapsedMilliseconds < PropagationTimeoutMs)
			{
				await Task.Delay(100);
			}
			// The scene itself on failure: the text comparison names the first line that differs.
			await Assert.That(await DescribeAsync(client)).IsEqualTo(expected).Because($"a client holds every write, those during its snapshot included ({writes} writes; this client: {Replication(client).SnapshotsLoaded} snapshots, {Replication(client).FramesReceived} frames, {await CountEventsAsync(client)} log records)");
		}
		await Assert.That(writes).IsGreaterThan(0);
	}

	/// <summary>A client whose projection does not fold a snapshot never asks for one: it would move past the history and show nothing.</summary>
	[Test]
	public async Task A_client_whose_projection_cannot_fold_a_snapshot_replays()
	{
		var streamId = Guid.NewGuid();
		var master = await StartMasterAsync(streamId, mongo: false);
		await BuildSceneAsync(master, SceneNodes);

		var client = await StartClientAsync(master, streamId, configure: builder => builder.Services.RemoveAll<StreamSnapshotFolding>());

		await ExpectSameSceneAsync(client, master, "the client holds the master's scene");
		await Assert.That(Replication(client).SnapshotsLoaded).IsEqualTo(0);
		await Assert.That(Replication(client).FramesReceived).IsEqualTo(await CountEventsAsync(master));
	}

	[Test]
	public async Task A_snapshot_the_master_cannot_finish_is_declined_and_the_client_replays()
	{
		var streamId = Guid.NewGuid();
		var master = await StartMasterAsync(streamId, mongo: false, configure: builder => builder.Services.AddSingleton<IStreamSnapshotSource>(sp => new FailingSnapshotSource(new InMemoryStreamSnapshotSource(sp.GetRequiredService<IProjectionProvider>()), failAfter: 10)));
		await BuildSceneAsync(master, SceneNodes);

		var client = await StartClientAsync(master, streamId);

		await ExpectSameSceneAsync(client, master, "the client holds the master's scene");
		await Assert.That(Replication(client).SnapshotsLoaded).IsEqualTo(0);
		await Assert.That(Replication(client).FramesReceived).IsEqualTo(await CountEventsAsync(master));
	}

	[Test]
	public async Task A_snapshot_whose_schema_this_build_cannot_read_is_refused_and_the_client_replays()
	{
		var streamId = Guid.NewGuid();
		// The master maps a type the client's build does not: its header names it, and the client cannot read it exactly.
		var master = await StartMasterAsync(streamId, mongo: false, configure: builder => builder.Services.AddSbxSerializer(ser => ser.Map(150, typeof(TestTaggingComponent))));
		await BuildSceneAsync(master, SceneNodes);

		var client = await StartClientAsync(master, streamId);

		await ExpectSameSceneAsync(client, master, "the client holds the master's scene");
		await Assert.That(Replication(client).SnapshotsLoaded).IsEqualTo(0);
		await Assert.That(await CountEventsAsync(client)).IsEqualTo(await CountEventsAsync(master)).Because("the client replayed the stream, with no snapshot record in its log");
	}

	// ---------------------------------------------------------------- nodes

	async Task<SynqraTestNode> StartMasterAsync(Guid streamId, bool mongo, Action<IHostApplicationBuilder>? configure = null)
	{
		var master = new SynqraTestNode(streamId, builder =>
		{
			ConfigureScene(builder);
			configure?.Invoke(builder);
		}, masterHost: true, useRealEndpoint: true, mongoMaster: mongo, serveSnapshots: !mongo);
		await master.Started;
		return master;
	}

	/// <summary>A client naming its stream (SubscribeTo), so a cold one asks for a snapshot unless told not to.</summary>
	async Task<SynqraTestNode> StartClientAsync(SynqraTestNode master, Guid streamId, bool requestSnapshot = true, Action<IHostApplicationBuilder>? configure = null)
	{
		var client = new SynqraTestNode(streamId, builder =>
		{
			ConfigureScene(builder);
			configure?.Invoke(builder);
		})
		{
			Port = master.Port,
			HelloKind = ReplicationHelloKind.SubscribeTo,
			RequestSnapshot = requestSnapshot,
		};
		await client.Started;
		await WaitForOnlineAsync(client);
		return client;
	}

	void ConfigureScene(IHostApplicationBuilder builder)
	{
		builder.Services.AddTypeMetadataProvider(typeof(SnapshotSceneNode), typeof(SnapshotScenePosition), typeof(SnapshotSceneTag), typeof(SnapshotSceneEdge));
		builder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(120, typeof(SnapshotSceneNode));
			ser.Map(121, typeof(SnapshotScenePosition));
			ser.Map(122, typeof(SnapshotSceneTag));
			ser.Map(123, typeof(SnapshotSceneEdge));
		});
	}

	EventReplicationService Replication(SynqraTestNode node) => node.Host.Services.GetRequiredService<EventReplicationService>();

	/// <summary>Runs on the master's store in the master's stream: a Mongo-backed master's store serves every stream.</summary>
	async Task RunOnMasterAsync(SynqraTestNode master, Func<IObjectStore, Task> work)
	{
		using (master.EnterStream())
		{
			await work(master.StoreContext);
		}
	}

	// ---------------------------------------------------------------- the scene

	/// <summary>
	/// Nodes with a position and two tags each, a chain of weighted edges, then edits: a rename, a moved position, a removed tag, a
	/// retargeted edge and a deleted node. Returns the ids of the nodes still there.
	/// </summary>
	async Task<List<Guid>> BuildSceneAsync(SynqraTestNode master, int count)
	{
		var ids = new List<Guid>();
		await RunOnMasterAsync(master, async store =>
		{
			var tags = new List<Guid>();
			for (var i = 0; i < count; i++)
			{
				var id = store.IdProvider.CreateComponentId();
				ids.Add(id);
				await AddAsync(store, id, id, new SnapshotSceneNode { Name = $"node {i}", Size = i * 10 });
				await AddAsync(store, id, store.IdProvider.CreateComponentId(), new SnapshotScenePosition { X = i, Y = -i });
				await AddAsync(store, id, store.IdProvider.CreateComponentId(), new SnapshotSceneTag { Label = $"even {i % 2 == 0}" });
				var tag = store.IdProvider.CreateComponentId();
				tags.Add(tag);
				await AddAsync(store, id, tag, new SnapshotSceneTag { Label = $"tag {i}" });
			}
			var edges = new List<Guid>();
			for (var i = 0; i + 1 < count; i++)
			{
				edges.Add(await AddEdgeAsync(store, ids[i], ids[i + 1], i / 2.0));
			}
			await RenameAsync(store, ids[0], "the first node");
			await ChangeAsync<SnapshotSceneTag>(store, ids[1], tags[1], nameof(SnapshotSceneTag.Label), "relabelled");
			await DeleteAsync<SnapshotSceneTag>(store, ids[2], tags[2]);
			await ChangeAsync<SnapshotSceneEdge>(store, ids[3], edges[3], nameof(Link.TargetId), ids[0]);
			var deleted = ids[^1];
			await DeleteAsync<SnapshotSceneNode>(store, deleted, deleted);
			ids.Remove(deleted);
		});
		return ids;
	}

	Task AddAsync(IObjectStore store, Guid owner, Guid componentId, object data)
	{
		var node = store.TypeMetadataProvider.GetTypeMetadata(typeof(SnapshotSceneNode));
		return store.SubmitCommandAsync(new AddComponentCommand
		{
			TargetTypeId = node.TypeId,
			CollectionId = node.CollectionId,
			TargetId = owner,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(data.GetType()).TypeId,
			ComponentId = componentId,
			Data = data,
		});
	}

	async Task<Guid> AddEdgeAsync(IObjectStore store, Guid source, Guid target, double weight)
	{
		var id = store.IdProvider.CreateComponentId();
		await AddAsync(store, source, id, new SnapshotSceneEdge { SourceId = source, TargetId = target, Weight = weight });
		return id;
	}

	Task RenameAsync(IObjectStore store, Guid node, string name) => ChangeAsync<SnapshotSceneNode>(store, node, node, nameof(SnapshotSceneNode.Name), name);

	Task ChangeAsync<TComponent>(IObjectStore store, Guid owner, Guid componentId, string property, object? value)
	{
		var node = store.TypeMetadataProvider.GetTypeMetadata(typeof(SnapshotSceneNode));
		return store.SubmitCommandAsync(new ChangeComponentPropertyCommand
		{
			TargetTypeId = node.TypeId,
			CollectionId = node.CollectionId,
			TargetId = owner,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TComponent)).TypeId,
			ComponentId = componentId,
			PropertyName = property,
			NewValue = value,
		});
	}

	Task DeleteAsync<TComponent>(IObjectStore store, Guid owner, Guid componentId)
	{
		var node = store.TypeMetadataProvider.GetTypeMetadata(typeof(SnapshotSceneNode));
		return store.SubmitCommandAsync(new DeleteComponentCommand
		{
			TargetTypeId = node.TypeId,
			CollectionId = node.CollectionId,
			TargetId = owner,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TComponent)).TypeId,
			ComponentId = componentId,
		});
	}

	/// <summary>The node's scene as text, one line per node in id order: its data, its position, its tags and its edges.</summary>
	async Task<string> DescribeAsync(SynqraTestNode node)
	{
		if (node.StoreContext is IReplayProjection && node.Host.Services.GetService<EventReplicationService>() is null)
		{
			// An in-memory master folds peers' events into its projection on demand. A client folds on every EventsReceived already;
			// reading its JSON-lines log here as well would race the replication's appends to it (TODO.md).
			await node.Host.Services.GetRequiredService<IProjectionProvider>().GetAsync(node.StreamId);
		}
		var text = new StringBuilder();
		await RunOnMasterAsync(node, store =>
		{
			foreach (var (id, scene) in ReadNodes(store))
			{
				var components = ((IComponentContainer)scene).Components.ToList();
				var position = components.OfType<SnapshotScenePosition>().SingleOrDefault();
				var tags = components.OfType<SnapshotSceneTag>().Select(x => x.Label).Order();
				var edges = scene.Edges.Select(x => $"{x.TargetId}:{x.Weight}").Order();
				text.AppendLine($"{id} {scene.Name} {scene.Size} @{position?.X},{position?.Y} [{string.Join(", ", tags)}] -> [{string.Join(", ", edges)}]");
			}
			return Task.CompletedTask;
		});
		return text.ToString();
	}

	/// <summary>
	/// The nodes with their ids, in id order, read in one pass: a Mongo collection's Count is a query of its own, so a copy sized by
	/// it misses or overruns a root added in between. A node deleted while it is read leaves the store's tracking, and asking its id
	/// then throws: it is gone, and left out.
	/// </summary>
	List<(Guid Id, SnapshotSceneNode Node)> ReadNodes(IObjectStore store)
	{
		var nodes = new List<(Guid Id, SnapshotSceneNode Node)>();
		foreach (var node in store.GetCollection<SnapshotSceneNode>())
		{
			if (FindId(store, node) is Guid id)
			{
				nodes.Add((id, node));
			}
		}
		return nodes.OrderBy(x => x.Id).ToList();
	}

	Guid? FindId(IObjectStore store, object node)
	{
		Guid? id = null;
		try
		{
			id = store.GetId(node);
		}
		catch (InvalidOperationException)
		{
			// deleted while the scene was read
		}
		return id;
	}

	/// <summary>
	/// Waits until the client holds the master's scene. On failure it says why: the client's counters, the logs' sizes, the first line
	/// that differs, and what one more catch-up of the client's projection does (a catch-up that throws is otherwise never seen: a
	/// client starts it fire-and-forget on each EventsReceived).
	/// </summary>
	async Task ExpectSameSceneAsync(SynqraTestNode client, SynqraTestNode master, string what)
	{
		var sw = Stopwatch.StartNew();
		while (await DescribeAsync(client) != await DescribeAsync(master) && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		var expected = await DescribeAsync(master);
		var actual = await DescribeAsync(client);
		if (actual != expected)
		{
			string catchUp;
			try
			{
				await client.Host.Services.GetRequiredService<IProjectionProvider>().GetAsync(client.StreamId);
				catchUp = await DescribeAsync(client) == expected ? "one more catch-up brought it level" : "one more catch-up changed nothing";
			}
			catch (Exception ex)
			{
				catchUp = $"one more catch-up threw {ex.GetType().Name}: {ex.Message}";
			}
			var replication = Replication(client);
			await Assert.That(actual).IsEqualTo(expected).Because($"{what}: client online {replication.IsOnline}, {replication.SnapshotsLoaded} snapshots, {replication.FramesReceived} frames, {await CountEventsAsync(client)} log records; master {await CountEventsAsync(master)} events; {catchUp}");
		}
	}

	// ---------------------------------------------------------------- logs and runs

	const string LogFileKey = "Storage:JsonLinesStorage:FileName";

	async Task<int> CountEventsAsync(SynqraTestNode node, Guid before = default)
	{
		var count = 0;
		await foreach (var ev in node.Events.GetAllAsync())
		{
			if (before == default || ev.SequenceId.CompareBigEndian(before) <= 0)
			{
				count++;
			}
		}
		return count;
	}

	/// <summary>Stops a client node and returns where its local log is, for its next run.</summary>
	async Task<string> EndRunAsync(SynqraTestNode node)
	{
		var logFile = node.Host.Services.GetRequiredService<IConfiguration>()[LogFileKey] ?? throw new InvalidOperationException("The node has no JSON-lines log file.");
		await node.Host.StopAsync();
		await ((IAsyncDisposable)node.Host).DisposeAsync();
		return logFile;
	}

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
		while (!Replication(node).IsOnline)
		{
			if (sw.ElapsedMilliseconds > PropagationTimeoutMs && !Debugger.IsAttached)
			{
				throw new TimeoutException("Node did not come online within 30s");
			}
			await Task.Delay(100);
		}
	}

	/// <summary>The host's state store, in memory: what a client saved is what its next run loads.</summary>
	sealed class RecordingStateStore(EventReplicationState? remembered = null) : IEventReplicationStateStore
	{
		public EventReplicationState? Saved { get; private set; } = remembered;

		public Task<EventReplicationState?> LoadAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(Saved is null ? null : Copy(Saved));

		public Task SaveAsync(Guid streamId, EventReplicationState state, CancellationToken cancellationToken = default)
		{
			Saved = Copy(state);
			return Task.CompletedTask;
		}

		EventReplicationState Copy(EventReplicationState state) => new() { MyNodeId = state.MyNodeId, LastEventIdFromMe = state.LastEventIdFromMe, ServerWatermark = state.ServerWatermark };
	}

	/// <summary>A source whose cut breaks off after some records: the master has sent part of a snapshot when it fails.</summary>
	sealed class FailingSnapshotSource(IStreamSnapshotSource inner, int failAfter) : IStreamSnapshotSource
	{
		public async Task<SnapshotCut?> CutAsync(Guid streamId, CancellationToken cancellationToken = default)
			=> await inner.CutAsync(streamId, cancellationToken) is { } cut ? new SnapshotCut(cut.Watermark, BreakOff(cut.Records), cut.DisposeAsync) : null;

		async IAsyncEnumerable<SnapshotRecord> BreakOff(IAsyncEnumerable<SnapshotRecord> records)
		{
			var sent = 0;
			await foreach (var record in records)
			{
				if (++sent > failAfter)
				{
					throw new InvalidOperationException("the cut broke off");
				}
				yield return record;
			}
		}
	}
}
#endif
