using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ShutterSpool.Server.Composition;

internal sealed class PostgresReadinessCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
