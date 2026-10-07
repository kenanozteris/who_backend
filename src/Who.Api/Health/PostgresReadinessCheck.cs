using Microsoft.Extensions.Diagnostics.HealthChecks;
using Who.Infrastructure.Persistence;

namespace Who.Api.Health;

public sealed class PostgresReadinessCheck(WhoDbContext database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await database.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No connection string, credentials or provider exception in output.
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
        }
    }
}
