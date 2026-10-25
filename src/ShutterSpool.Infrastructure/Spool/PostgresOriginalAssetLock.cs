using Npgsql;
using ShutterSpool.Application.Spool;

namespace ShutterSpool.Infrastructure.Spool;

internal sealed class PostgresOriginalAssetLock(string connectionString) : IOriginalAssetLock
{
    public Task<IOriginalAssetLockLease> AcquireSharedAsync(Guid assetId, string storageKey, CancellationToken cancellationToken) =>
        AcquireAsync(assetId, storageKey, shared: true, cancellationToken);

    public Task<IOriginalAssetLockLease> AcquireExclusiveAsync(Guid assetId, string storageKey, CancellationToken cancellationToken) =>
        AcquireAsync(assetId, storageKey, shared: false, cancellationToken);

    public async Task<bool> IsAvailableAsync(IOriginalAssetLockLease sharedLease, CancellationToken cancellationToken)
    {
        Lease lease = RequireSharedLease(sharedLease);
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM assets AS asset
                WHERE asset."Id" = @asset_id AND asset."SpoolKey" = @storage_key AND asset."State" = 'Spooled'
                  AND NOT EXISTS (
                      SELECT 1 FROM asset_removal_operations AS removal WHERE removal."AssetId" = asset."Id")
                  AND NOT EXISTS (
                      SELECT 1 FROM privacy_erasure_assets AS erased WHERE erased."AssetId" = asset."Id")
                  AND NOT EXISTS (
                      SELECT 1 FROM capture_assets AS membership
                      JOIN captures AS capture ON capture."Id" = membership."CaptureId"
                      WHERE membership."AssetId" = asset."Id"
                        AND capture."RetentionDisposition" NOT IN ('Active', 'Trash'))
            )
            """;
        command.Parameters.AddWithValue("asset_id", lease.AssetId);
        command.Parameters.AddWithValue("storage_key", lease.StorageKey);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<IOriginalAssetLockLease> AcquireAsync(Guid assetId, string storageKey, bool shared,
        CancellationToken cancellationToken)
    {
        if (assetId == Guid.Empty) throw new ArgumentException("An Asset identity is required.", nameof(assetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        if (storageKey.Length > 1_024) throw new ArgumentOutOfRangeException(nameof(storageKey));

        string lockKey = $"shutterspool-original:{assetId:N}:{storageKey}";
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = shared
                ? "SELECT pg_advisory_lock_shared(hashtextextended(@lock_key, 0))"
                : "SELECT pg_advisory_lock(hashtextextended(@lock_key, 0))";
            command.Parameters.AddWithValue("lock_key", lockKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new Lease(connection, assetId, storageKey, lockKey, shared);
        }
        catch
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                try { await UnlockAsync(connection, lockKey, shared); }
                catch { }
            }
            await connection.DisposeAsync();
            throw;
        }
    }

    private static Lease RequireSharedLease(IOriginalAssetLockLease lease) =>
        lease is Lease { IsShared: true, IsDisposed: false } owned
            ? owned
            : throw new InvalidOperationException("A live shared original lock is required.");

    private static async Task UnlockAsync(NpgsqlConnection connection, string lockKey, bool shared)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = shared
            ? "SELECT pg_advisory_unlock_shared(hashtextextended(@lock_key, 0))"
            : "SELECT pg_advisory_unlock(hashtextextended(@lock_key, 0))";
        command.Parameters.AddWithValue("lock_key", lockKey);
        if ((bool)(await command.ExecuteScalarAsync(CancellationToken.None))! != true)
            throw new InvalidOperationException("The original storage lock was not owned by this PostgreSQL session.");
    }

    private sealed class Lease : IOriginalAssetLockLease
    {
        private readonly string _lockKey;
        private readonly bool _isShared;
        private NpgsqlConnection? _connection;
        public Lease(NpgsqlConnection connection, Guid assetId, string storageKey, string lockKey, bool isShared)
        {
            _connection = connection;
            AssetId = assetId;
            StorageKey = storageKey;
            _lockKey = lockKey;
            _isShared = isShared;
        }

        public Guid AssetId { get; }
        public string StorageKey { get; }
        public bool IsShared => _isShared;
        public bool IsDisposed => Volatile.Read(ref _connection) is null;
        public NpgsqlConnection Connection => Volatile.Read(ref _connection)
            ?? throw new ObjectDisposedException(nameof(Lease));

        public void Dispose()
        {
            NpgsqlConnection? owned = Interlocked.Exchange(ref _connection, null);
            if (owned is null) return;
            try { Unlock(owned, _lockKey, _isShared); }
            finally { owned.Dispose(); }
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection? owned = Interlocked.Exchange(ref _connection, null);
            if (owned is null) return;
            try { await UnlockAsync(owned, _lockKey, _isShared); }
            finally { await owned.DisposeAsync(); }
        }
    }

    private static void Unlock(NpgsqlConnection connection, string lockKey, bool shared)
    {
        using var command = connection.CreateCommand();
        command.CommandText = shared
            ? "SELECT pg_advisory_unlock_shared(hashtextextended(@lock_key, 0))"
            : "SELECT pg_advisory_unlock(hashtextextended(@lock_key, 0))";
        command.Parameters.AddWithValue("lock_key", lockKey);
        if ((bool)command.ExecuteScalar()! != true)
            throw new InvalidOperationException("The original storage lock was not owned by this PostgreSQL session.");
    }
}
