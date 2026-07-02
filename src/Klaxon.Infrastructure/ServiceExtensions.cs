using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klaxon.Infrastructure;

public static class ServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<KlaxonDbContext>((sp, options) =>
            options.UseNpgsql(
                sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres"),
                npgsql =>
                {
                    npgsql.UseNodaTime();
                    // Rides out transient drops — notably the first request after a serverless
                    // Postgres cold-start — instead of surfacing a one-off 500.
                    npgsql.EnableRetryOnFailure();
                }));

        return services;
    }
}
