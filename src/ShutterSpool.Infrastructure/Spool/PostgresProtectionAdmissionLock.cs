using Npgsql;
using NpgsqlTypes;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Spool;

namespace ShutterSpool.Infrastructure.Spool;

internal sealed class PostgresProtectionAdmissionLock(string connectionString, ISpool spool) : IProtectionAdmissionLock
{
    private readonly ICommittedOriginalReader _spool = spool as ICommittedOriginalReader
        ?? throw new InvalidOperationException("The configured spool cannot read committed originals.");

    public async Task<IProtectionAdmissionLease> AcquireSharedAsync(IReadOnlyList<ProtectionOriginal> originals,
        CancellationToken cancellationToken)
    {
        if (originals.Count is < 1 or > AssetProtectionService.MaximumAssetCount
            || originals.Select(item => item.AssetId).Distinct().Count() != originals.Count)
            throw new ArgumentException("A bounded distinct original set is required.", nameof(originals));
        ProtectionOriginal[] ordered = [.. originals.OrderBy(item => item.AssetId)];
        var connection = new NpgsqlConnection(connectionString);
        var acquired = new List<string>(ordered.Length);
        try
        {
            await connection.OpenAsync(cancellationToken);
            foreach (ProtectionOriginal original in ordered)
            {
                string key = LockKey(original);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_lock_shared(hashtextextended(@lock_key, 0))";
                command.Parameters.AddWithValue("lock_key", key);
                await command.ExecuteNonQueryAsync(cancellationToken);
                acquired.Add(key);
            }
            return new Lease(connection, ordered, acquired, _spool);
        }
        catch
        {
            await UnlockAllAsync(connection, acquired);
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string LockKey(ProtectionOriginal original) =>
        $"shutterspool-original:{original.AssetId:N}:{original.SpoolKey}";

    private static async Task UnlockAllAsync(NpgsqlConnection connection, IReadOnlyList<string> keys)
    {
        for (int index = keys.Count - 1; index >= 0; index--)
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock_shared(hashtextextended(@lock_key, 0))";
                command.Parameters.AddWithValue("lock_key", keys[index]);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            catch { }
        }
    }

    private sealed class Lease(NpgsqlConnection connection, ProtectionOriginal[] originals,
        List<string> lockKeys, ICommittedOriginalReader spool) : IProtectionAdmissionLease
    {
        private NpgsqlConnection? _connection = connection;

        public async Task<bool> AreAvailableAsync(CancellationToken cancellationToken)
        {
            NpgsqlConnection owned = Volatile.Read(ref _connection)
                ?? throw new ObjectDisposedException(nameof(Lease));
            await using var command = owned.CreateCommand();
            command.CommandText = """
                SELECT count(*)
                FROM unnest(@asset_ids, @capture_ids, @spool_keys, @sizes)
                    AS expected(asset_id, capture_id, spool_key, size)
                JOIN assets AS asset ON asset."Id" = expected.asset_id
                    AND asset."SpoolKey" = expected.spool_key AND asset."Size" = expected.size
                JOIN capture_assets AS membership ON membership."AssetId" = asset."Id"
                    AND membership."CaptureId" = expected.capture_id
                JOIN captures AS capture ON capture."Id" = membership."CaptureId"
                WHERE asset."State" = 'Spooled'
                  AND capture."RetentionDisposition" IN ('Active', 'Trash')
                  AND NOT EXISTS (
                      SELECT 1 FROM privacy_erasure_assets AS erased WHERE erased."AssetId" = asset."Id")
                  AND NOT EXISTS (
                      SELECT 1 FROM asset_removal_operations AS removal WHERE removal."AssetId" = asset."Id")
                """;
            command.Parameters.AddWithValue("asset_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                originals.Select(item => item.AssetId).ToArray());
            command.Parameters.AddWithValue("capture_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                originals.Select(item => item.CaptureId).ToArray());
            command.Parameters.AddWithValue("spool_keys", NpgsqlDbType.Array | NpgsqlDbType.Text,
                originals.Select(item => item.SpoolKey).ToArray());
            command.Parameters.AddWithValue("sizes", NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                originals.Select(item => item.Size).ToArray());
            long available = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            if (available != originals.Length) return false;
            foreach (ProtectionOriginal original in originals)
            {
                try
                {
                    await using Stream bytes = await spool.OpenReadAsync(original.SpoolKey, cancellationToken);
                    if (!bytes.CanRead || !bytes.CanSeek || bytes.Length != original.Size) return false;
                }
                catch (IOException) { return false; }
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection? owned = Interlocked.Exchange(ref _connection, null);
            if (owned is null) return;
            await UnlockAllAsync(owned, lockKeys);
            await owned.DisposeAsync();
        }
    }
}
