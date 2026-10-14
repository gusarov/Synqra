using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.Tests.TestHelpers;

/// <summary>
/// Makes the test server a usable one-node replica set: initiates a server started with
/// <c>--replSet</c> that has no configuration yet, then waits until it is a writable primary.
/// Talks to the one host directly, since an uninitiated member is not discoverable as a set.
/// </summary>
internal sealed class ReplicaSetInitiator(string connectionString)
{
	public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

	public void EnsureInitiated()
	{
		var url = new MongoUrl(connectionString);
		var direct = new MongoUrlBuilder(connectionString) { DirectConnection = true, ReplicaSetName = null };
		using var client = new MongoClient(direct.ToMongoUrl());
		var admin = client.GetDatabase("admin");
		var hello = admin.RunCommand<BsonDocument>(new BsonDocument("hello", 1));
		if (!(hello.Contains("setName") && hello["isWritablePrimary"].ToBoolean()))
		{
			var options = admin.RunCommand<BsonDocument>(new BsonDocument("getCmdLineOpts", 1));
			var replication = options["parsed"].AsBsonDocument.GetValue("replication", null)?.AsBsonDocument;
			var setName = replication?.GetValue("replSetName", null)?.AsString ?? replication?.GetValue("replSet", null)?.AsString;
			if (setName is null)
			{
				throw new InvalidOperationException($"The test Mongo at {url.Server} is a standalone server; Synqra's Mongo writes run in transactions and need a replica set. Start mongod with --replSet <name> (or set replication.replSetName in its config); the tests initiate it.");
			}
			if (!hello.Contains("setName"))
			{
				var config = new BsonDocument
				{
					["_id"] = setName,
					["members"] = new BsonArray { new BsonDocument { ["_id"] = 0, ["host"] = url.Server.ToString() } },
				};
				try
				{
					admin.RunCommand<BsonDocument>(new BsonDocument("replSetInitiate", config));
				}
				catch (MongoCommandException ex) when (ex.CodeName == "AlreadyInitialized")
				{
					// Another test process (net8/net9/net10 runs overlap) initiated it first; wait for primary below.
				}
			}
			var deadline = DateTime.UtcNow + Timeout;
			while (!admin.RunCommand<BsonDocument>(new BsonDocument("hello", 1)).GetValue("isWritablePrimary", false).ToBoolean())
			{
				if (DateTime.UtcNow > deadline)
				{
					throw new TimeoutException($"Replica set '{setName}' at {url.Server} did not elect a primary within {Timeout}.");
				}
				Thread.Sleep(200);
			}
		}
	}
}
