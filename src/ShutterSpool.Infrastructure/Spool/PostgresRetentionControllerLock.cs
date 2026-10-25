using Npgsql;
using ShutterSpool.Application.Retention;

namespace ShutterSpool.Infrastructure.Spool;

internal sealed class PostgresRetentionControllerLock(string connectionString) : IRetentionControllerLock
{
    private const string LockKey = "shutterspool-retention-controller";

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(hashtextextended(@lock_key, 0))";
            command.Parameters.AddWithValue("lock_key", LockKey);
            bool acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            if (!acquired)
            {
                await connection.DisposeAsync();
                return null;
            }
            return new Lease(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(NpgsqlConnection connection) : IAsyncDisposable
    {
        private NpgsqlConnection? _connection = connection;

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection? owned = Interlocked.Exchange(ref _connection, null);
            if (owned is null) return;
            try
            {
                await using var command = owned.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@lock_key, 0))";
                command.Parameters.AddWithValue("lock_key", LockKey);
                if ((bool)(await command.ExecuteScalarAsync(CancellationToken.None))! != true)
                    throw new InvalidOperationException("The retention controller lease was not owned by this PostgreSQL session.");
            }
            finally
            {
                await owned.DisposeAsync();
            }
        }
    }
}
