using MongoDB.Bson;
using MongoDB.Driver;
using Synqra;

namespace Contoso.WebHost;

/// <summary>The master's store for one stream, for the server's own writes and reads (REST): whichever store the master runs.</summary>
public interface IContosoMasterStore
{
	Task<T> RunAsync<T>(Guid streamId, Func<IObjectStore, Task<T>> work);
}

/// <summary>An in-memory master: the stream's projection from the provider, caught up with the log first.</summary>
public sealed class InMemoryContosoMasterStore(IProjectionProvider provider) : IContosoMasterStore
{
	public async Task<T> RunAsync<T>(Guid streamId, Func<IObjectStore, Task<T>> work)
		=> await work(await provider.GetAsync(streamId));
}

/// <summary>A Mongo-backed master: the one multitenant store, scoped to the stream for the call, as the replication endpoint scopes a connection.</summary>
public sealed class MongoContosoMasterStore(IObjectStore store) : IContosoMasterStore
{
	public async Task<T> RunAsync<T>(Guid streamId, Func<IObjectStore, Task<T>> work)
	{
		using (SynqraStreamContext.Enter(streamId))
		{
			return await work(store);
		}
	}
}

/// <summary>
/// Makes a server started with <c>--replSet</c> (CI's <c>withmongo</c>) a writable one-node replica set: Synqra's Mongo writes run in
/// transactions and its snapshots read through snapshot sessions, which a standalone refuses. A set already up is left alone.
/// </summary>
public sealed class ContosoMongoReplicaSet(string connectionString)
{
	public void EnsureInitiated()
	{
		var url = new MongoUrl(connectionString);
		using var client = new MongoClient(new MongoUrlBuilder(connectionString) { DirectConnection = true, ReplicaSetName = null }.ToMongoUrl());
		var admin = client.GetDatabase("admin");
		var hello = admin.RunCommand<BsonDocument>(new BsonDocument("hello", 1));
		if (!hello.Contains("setName"))
		{
			var parsed = admin.RunCommand<BsonDocument>(new BsonDocument("getCmdLineOpts", 1))["parsed"].AsBsonDocument;
			var replication = parsed.GetValue("replication", null)?.AsBsonDocument;
			var setName = replication?.GetValue("replSetName", null)?.AsString ?? replication?.GetValue("replSet", null)?.AsString
				?? throw new InvalidOperationException($"Contoso: the Mongo at {url.Server} is a standalone server; start mongod with --replSet <name>.");
			try
			{
				admin.RunCommand<BsonDocument>(new BsonDocument("replSetInitiate", new BsonDocument
				{
					["_id"] = setName,
					["members"] = new BsonArray { new BsonDocument { ["_id"] = 0, ["host"] = url.Server.ToString(), }, },
				}));
			}
			catch (MongoCommandException ex) when (ex.CodeName == "AlreadyInitialized")
			{
				// another process initiated it first
			}
		}
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (!admin.RunCommand<BsonDocument>(new BsonDocument("hello", 1)).GetValue("isWritablePrimary", false).ToBoolean())
		{
			if (DateTime.UtcNow > deadline)
			{
				throw new TimeoutException($"Contoso: the Mongo replica set at {url.Server} elected no primary within 30 s.");
			}
			Thread.Sleep(200);
		}
	}
}
