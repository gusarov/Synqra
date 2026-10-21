using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Net;
using System.Net.Sockets;
using Synqra.AppendStorage.MongoDb;
using Synqra.Projection.MongoDb;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// A Mongo projection over a non-Mongo event log (no Mongo event storage registered) still gets its
/// projection upgrades from <see cref="SynqraMongoUpgradeService"/>, claimed in the projection database.
/// </summary>
public class MongoProjectionOnlyUpgradeTests : BaseTest
{
	IMongoDatabase OpenFreshDatabase()
	{
		return new MongoClient(new MongoUrl(_connectionString)).GetDatabase("projonly_" + Guid.NewGuid().ToString("N"));
	}

	SynqraMongoUpgradeService Runner(IMongoDatabase db, bool withEventStorage)
	{
		var services = new ServiceCollection();
		services.AddSingleton(db);
		if (withEventStorage)
		{
			services.AddMongoEventStorage();
		}
		services.AddMongoProjection();
		return services.BuildServiceProvider().GetServices<IHostedService>().OfType<SynqraMongoUpgradeService>().Single();
	}

	[Test]
	public async Task Should_create_components_and_text_indexes_for_a_projection_without_mongo_event_storage()
	{
		var db = OpenFreshDatabase();
		try
		{
			await Runner(db, withEventStorage: false).StartAsync(CancellationToken.None);

			var indexes = (await db.GetCollection<BsonDocument>("Components").Indexes.List().ToListAsync()).Select(i => i["name"].AsString).ToList();
			await Assert.That(indexes).Contains("components_text");
			await Assert.That(indexes.Count(n => n.StartsWith("_sid_1"))).IsGreaterThanOrEqualTo(4);

			var claims = await db.GetCollection<BsonDocument>("_synqra_upgrades").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
			await Assert.That(claims.Select(c => c["_id"].AsString).Order().ToList()).IsEquivalentTo(["components-indexes/3", "components-text/1"]);
			await Assert.That(claims.All(c => c.Contains("completedAt"))).IsTrue();

			// Second boot: completed claims short-circuit.
			await Runner(db, withEventStorage: false).StartAsync(CancellationToken.None);
			await Assert.That(await db.GetCollection<BsonDocument>("_synqra_upgrades").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).IsEqualTo(2L);
		}
		finally
		{
			await db.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
		}
	}

	[Test]
	public async Task Should_run_each_upgrade_once_when_the_key_has_both_event_storage_and_projection()
	{
		var db = OpenFreshDatabase();
		try
		{
			await Runner(db, withEventStorage: true).StartAsync(CancellationToken.None);

			var claims = await db.GetCollection<BsonDocument>("_synqra_upgrades").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
			await Assert.That(claims.Select(c => c["_id"].AsString).Distinct().Count()).IsEqualTo(claims.Count);
			await Assert.That(claims.Count(c => c["_id"].AsString.StartsWith("components-"))).IsEqualTo(2);
			await Assert.That(claims.All(c => c.Contains("completedAt"))).IsTrue();
		}
		finally
		{
			await db.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
		}
	}

	[Test]
	public async Task Should_upgrade_a_projection_whose_server_answers_slowly_rather_than_skip_it()
	{
		var target = new MongoUrl(_connectionString).Server;
		await using var proxy = new DelayingTcpProxy(target.Host, target.Port, firstConnectionDelay: TimeSpan.FromSeconds(6));
		var url = new MongoUrlBuilder(_connectionString)
		{
			Server = new MongoServerAddress("127.0.0.1", proxy.Port),
			DirectConnection = true,
			ReplicaSetName = null,
		};
		var db = new MongoClient(url.ToMongoUrl()).GetDatabase("projonly_" + Guid.NewGuid().ToString("N"));
		try
		{
			await Runner(db, withEventStorage: false).StartAsync(CancellationToken.None);

			var indexes = (await db.GetCollection<BsonDocument>("Components").Indexes.List().ToListAsync()).Select(i => i["name"].AsString).ToList();
			await Assert.That(indexes).Contains("components_text");
		}
		finally
		{
			await db.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
		}
	}

	/// <summary>Passes TCP through to a real server, holding the first connection back: a server that is up but slow to answer.</summary>
	sealed class DelayingTcpProxy : IAsyncDisposable
	{
		readonly TcpListener _listener = new(IPAddress.Loopback, 0);
		readonly CancellationTokenSource _stop = new();
		readonly List<Task> _connections = [];
		readonly string _targetHost;
		readonly int _targetPort;
		readonly TimeSpan _firstConnectionDelay;
		readonly Task _accepting;

		public DelayingTcpProxy(
			  string targetHost
			, int targetPort
			, TimeSpan firstConnectionDelay
		)
		{
			_targetHost = targetHost;
			_targetPort = targetPort;
			_firstConnectionDelay = firstConnectionDelay;
			_listener.Start();
			_accepting = AcceptAsync();
		}

		public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

		async Task AcceptAsync()
		{
			var delay = _firstConnectionDelay;
			try
			{
				while (!_stop.IsCancellationRequested)
				{
					var inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
					_connections.Add(PipeAsync(inbound, delay));
					delay = TimeSpan.Zero;
				}
			}
			catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
			{
				// stopped
			}
		}

		async Task PipeAsync(TcpClient inbound, TimeSpan delay)
		{
			using var outbound = new TcpClient();
			try
			{
				using (inbound)
				{
					await Task.Delay(delay, _stop.Token);
					await outbound.ConnectAsync(_targetHost, _targetPort, _stop.Token);
					var client = inbound.GetStream();
					var server = outbound.GetStream();
					await Task.WhenAny(client.CopyToAsync(server, _stop.Token), server.CopyToAsync(client, _stop.Token));
				}
			}
			catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
			{
				// a side hung up, or the proxy stopped
			}
		}

		public async ValueTask DisposeAsync()
		{
			_stop.Cancel();
			_listener.Stop();
			await _accepting;
			await Task.WhenAll(_connections);
			_stop.Dispose();
		}
	}
}
