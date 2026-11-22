using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ShutterSpool.Server.Composition;

internal sealed class PostgresReadinessCheck(NpgsqlDataSource dataSource) : IHealthCheck, IDisposable
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }

    public void Dispose() => dataSource.Dispose();
}
