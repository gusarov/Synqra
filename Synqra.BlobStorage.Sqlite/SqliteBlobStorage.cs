using Microsoft.Data.Sqlite;
using System.Data;
using System.Runtime.CompilerServices;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.Sqlite;

public class SqliteBlobStorage<TKey> : IBlobStorage<TKey>
	where TKey : notnull, IComparable<TKey>
{
	private readonly SqliteConnection _connection;
	private readonly string _storeName;

	public SqliteBlobStorage(SqliteBlobStorageOptions options, string storeName)
	{
		_storeName = storeName;
		_connection = new SqliteConnection(options.ConnectionString);
		_connection.Open();

		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS blobs (
				store_name TEXT NOT NULL,
				id         BLOB NOT NULL,
				data       BLOB NOT NULL,
				PRIMARY KEY (store_name, id)
			) WITHOUT ROWID;
			""";
		cmd.ExecuteNonQuery();

		using var pragma = _connection.CreateCommand();
		pragma.CommandText = """
			PRAGMA journal_mode = WAL;
			PRAGMA synchronous = NORMAL;
			""";
		pragma.ExecuteNonQuery();
	}

	public bool SupportsSyncOperations => true;

	public ValueTask<byte[]> ReadBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = "SELECT data FROM blobs WHERE store_name = @store_name AND id = @id";
		cmd.Parameters.AddWithValue("@store_name", _storeName);
		cmd.Parameters.AddWithValue("@id", EncodeKey(key));
		var result = cmd.ExecuteScalar();
		if (result is byte[] bytes)
		{
			return ValueTask.FromResult(bytes);
		}

		throw new KeyNotFoundException("Blob is not found for key " + key);
	}

	public ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = "INSERT INTO blobs (store_name, id, data) VALUES (@store_name, @id, @data)";
		cmd.Parameters.AddWithValue("@store_name", _storeName);
		cmd.Parameters.AddWithValue("@id", EncodeKey(key));
		cmd.Parameters.AddWithValue("@data", blob.ToArray());
		cmd.ExecuteNonQuery();
		return ValueTask.CompletedTask;
	}

	public ValueTask DeleteBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = "DELETE FROM blobs WHERE store_name = @store_name AND id = @id";
		cmd.Parameters.AddWithValue("@store_name", _storeName);
		cmd.Parameters.AddWithValue("@id", EncodeKey(key));
		cmd.ExecuteNonQuery();
		return ValueTask.CompletedTask;
	}

	public async IAsyncEnumerable<TKey> EnumerateKeysAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		using var cmd = _connection.CreateCommand();

		if (from is not null && !Equals(from, default(TKey)))
		{
			cmd.CommandText = "SELECT id FROM blobs WHERE store_name = @store_name AND id >= @from ORDER BY id";
			cmd.Parameters.AddWithValue("@from", EncodeKey(from));
		}
		else
		{
			cmd.CommandText = "SELECT id FROM blobs WHERE store_name = @store_name ORDER BY id";
		}

		cmd.Parameters.AddWithValue("@store_name", _storeName);

		using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var blob = (byte[])reader.GetValue(0);
			yield return DecodeKey(blob);
		}
	}

	public void WriteBlob(TKey key, ReadOnlySpan<byte> blob)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = "INSERT INTO blobs (store_name, id, data) VALUES (@store_name, @id, @data)";
		cmd.Parameters.AddWithValue("@store_name", _storeName);
		cmd.Parameters.AddWithValue("@id", EncodeKey(key));
		cmd.Parameters.AddWithValue("@data", blob.ToArray());
		cmd.ExecuteNonQuery();
	}

	public void DeleteBlob(TKey key)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = "DELETE FROM blobs WHERE store_name = @store_name AND id = @id";
		cmd.Parameters.AddWithValue("@store_name", _storeName);
		cmd.Parameters.AddWithValue("@id", EncodeKey(key));
		cmd.ExecuteNonQuery();
	}

	private static byte[] EncodeKey(TKey key)
	{
		if (key is Guid guid)
		{
			Span<byte> bytes = stackalloc byte[16];
			WriteBigEndianGuid(guid, bytes);
			return bytes.ToArray();
		}

		throw new NotSupportedException($"SqliteBlobStorage only supports Guid keys. Actual: {typeof(TKey)}");
	}

	private static TKey DecodeKey(ReadOnlySpan<byte> bytes)
	{
		if (typeof(TKey) == typeof(Guid))
		{
			Span<byte> copy = stackalloc byte[16];
			bytes.CopyTo(copy);
			SwapGuidEndian(copy);
			var guid = new Guid(copy);
			return (TKey)(object)guid;
		}

		throw new NotSupportedException($"SqliteBlobStorage only supports Guid keys. Actual: {typeof(TKey)}");
	}

	private static void WriteBigEndianGuid(Guid guid, Span<byte> bytes)
	{
		guid.TryWriteBytes(bytes);
		SwapGuidEndian(bytes);
	}

	private static void SwapGuidEndian(Span<byte> bytes)
	{
		(bytes[0], bytes[3]) = (bytes[3], bytes[0]);
		(bytes[1], bytes[2]) = (bytes[2], bytes[1]);
		(bytes[4], bytes[5]) = (bytes[5], bytes[4]);
		(bytes[6], bytes[7]) = (bytes[7], bytes[6]);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	public ValueTask DisposeAsync()
	{
		return _connection.DisposeAsync();
	}
}
