using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Synqra.BinarySerializer;

namespace Synqra.AppendStorage.Sqlite;

public class SqliteAppendStorage<T, TKey> : IAppendStorage<T, TKey>, IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ISBXSerializer _serializer;
    private readonly Func<T, Guid> _getKey;
    private readonly object _lock = new();

    public SqliteAppendStorage(
        string connectionString,
        ISBXSerializerFactory serializerFactory,
        Func<T, Guid> getKey)
    {
        _serializer = serializerFactory.CreateSerializer();
        _getKey = getKey;
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                id   BLOB PRIMARY KEY,
                data BLOB NOT NULL
            ) WITHOUT ROWID;
            """;
        cmd.ExecuteNonQuery();

        // Tune for append-heavy workload
        using var pragma = _connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            """;
        pragma.ExecuteNonQuery();
    }

    public Task<string> TestAsync(string input) => Task.FromResult(input);

    public Task AppendAsync(T item, CancellationToken cancellationToken = default)
    {
        var guid = _getKey(item);
        var keyBytes = GuidToBigEndianBytes(guid);
        var dataBytes = SerializeItem(item);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO events (id, data) VALUES (@id, @data)";
            cmd.Parameters.AddWithValue("@id", keyBytes);
            cmd.Parameters.AddWithValue("@data", dataBytes);
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO events (id, data) VALUES (@id, @data)";
            var idParam = cmd.Parameters.Add("@id", SqliteType.Blob);
            var dataParam = cmd.Parameters.Add("@data", SqliteType.Blob);

            foreach (var item in items)
            {
                var guid = _getKey(item);
                idParam.Value = GuidToBigEndianBytes(guid);
                dataParam.Value = SerializeItem(item);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<T> GetAllAsync(
        TKey? from = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();

        if (from is Guid g && g != Guid.Empty)
        {
            cmd.CommandText = "SELECT data FROM events WHERE id >= @from ORDER BY id";
            cmd.Parameters.AddWithValue("@from", GuidToBigEndianBytes(g));
        }
        else
        {
            cmd.CommandText = "SELECT data FROM events ORDER BY id";
        }

        using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var blob = (byte[])reader.GetValue(0);
            yield return DeserializeItem(blob);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // WAL mode handles durability; explicit checkpoint if needed
        return Task.CompletedTask;
    }

    private byte[] SerializeItem(T item)
    {
        var buffer = new byte[4096]; // TODO: pool / resize
        int pos = 0;
        _serializer.Serialize(buffer.AsSpan(), item, ref pos);
        return buffer.AsSpan(0, pos).ToArray();
    }

    private T DeserializeItem(byte[] data)
    {
        int pos = 0;
        return _serializer.Deserialize<T>(data.AsSpan(), ref pos);
    }

    /// <summary>
    /// Converts Guid to big-endian 16 bytes so SQLite BLOB comparison
    /// preserves v7 chronological order.
    /// </summary>
    private static byte[] GuidToBigEndianBytes(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        // .NET Guid layout on little-endian: int(LE) short(LE) short(LE) 8-bytes(BE)
        // Swap first 4 bytes
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        // Swap bytes 4-5
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        // Swap bytes 6-7
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
        // Bytes 8-15 are already big-endian
        return bytes.ToArray();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return default;
    }
}