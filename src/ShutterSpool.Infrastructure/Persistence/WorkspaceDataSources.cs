using Npgsql;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class WorkspaceDataSources : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NpgsqlDataSource> _sources = new(StringComparer.Ordinal);
    private bool _disposed;

    internal NpgsqlDataSource Get(string connectionString)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sources.TryGetValue(connectionString, out NpgsqlDataSource? source)) return source;

            source = NpgsqlDataSource.Create(connectionString);
            _sources.Add(connectionString, source);
            return source;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (NpgsqlDataSource source in _sources.Values) source.Dispose();
            _sources.Clear();
        }
    }
}
