using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.Tests.TestHelpers;

/// <summary>
/// Drops stale temp databases left by earlier test runs. Test processes (net8/net9/net10, parallel
/// runners) sweep the same server at once, and mongod refuses a second drop of a database that is
/// still being dropped; so each drop is claimed first with an insert keyed by the database name,
/// and only the process whose insert wins drops it. Claims expire, so a claimant that died
/// mid-drop does not hold the database forever.
/// </summary>
public sealed class TempDatabaseSweeper(IMongoClient client, string prefix)
{
	public const string ClaimsDatabaseName = "xkit_ephemeral_maintenance";

	public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(1);

	public TimeSpan ClaimLifetime { get; init; } = TimeSpan.FromMinutes(10);

	/// <summary>Number of databases this sweep dropped (others may have been dropped by a concurrent sweep).</summary>
	public int Sweep(DateTime utcNow)
	{
		var dropped = 0;
		var claims = OpenClaims();
		foreach (var databaseName in client.ListDatabaseNames().ToList())
		{
			if (databaseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && IsStale(databaseName, utcNow) && TryClaim(claims, databaseName, utcNow))
			{
				client.DropDatabase(databaseName);
				dropped++;
			}
		}
		return dropped;
	}

	IMongoCollection<BsonDocument> OpenClaims()
	{
		var claims = client.GetDatabase(ClaimsDatabaseName).GetCollection<BsonDocument>("drops");
		claims.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
			Builders<BsonDocument>.IndexKeys.Ascending("at"),
			new CreateIndexOptions { ExpireAfter = ClaimLifetime })); // same spec every time, so concurrent creates agree
		return claims;
	}

	bool TryClaim(IMongoCollection<BsonDocument> claims, string databaseName, DateTime utcNow)
	{
		var claimed = true;
		try
		{
			claims.InsertOne(new BsonDocument { ["_id"] = databaseName, ["at"] = utcNow, ["pid"] = Environment.ProcessId });
		}
		catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
		{
			Console.WriteLine($"EphemeralMongo: {databaseName} is being dropped by another sweep ({ex.WriteError.Message})"); // losing the race is expected: the winner drops it
			claimed = false;
		}
		return claimed;
	}

	bool IsStale(string databaseName, DateTime utcNow)
	{
		bool stale;
		if (Guid.TryParse(databaseName[prefix.Length..].Replace('_', '-'), out var guid) && guid.GetVersion() == 7)
		{
			stale = utcNow - guid.GetTimestamp() > StaleAfter;
		}
		else
		{
			var marker = client.GetDatabase(databaseName).GetCollection<BsonDocument>("_xkit");
			var stamp = marker.Find(Builders<BsonDocument>.Filter.Eq("_id", "EphemeralMongoStamp")).FirstOrDefault();
			stale = false;
			if (stamp is null)
			{
				try
				{
					marker.InsertOne(new BsonDocument { ["_id"] = "EphemeralMongoStamp", ["seen"] = utcNow });
				}
				catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
				{
					Console.WriteLine($"EphemeralMongo: {databaseName} was stamped by another sweep ({ex.WriteError.Message})"); // fine, the next sweep compares against whatever timestamp won
				}
			}
			else
			{
				stale = utcNow - stamp["seen"].ToUniversalTime() > StaleAfter;
			}
		}
		return stale;
	}
}
