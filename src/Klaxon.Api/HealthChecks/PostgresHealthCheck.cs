using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Klaxon.Api.HealthChecks;

// Readiness signal: can the app reach its database? Tagged "ready" so it feeds /healthz/ready.
public sealed class PostgresHealthCheck(KlaxonDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Postgres is not reachable.");
    }
}
