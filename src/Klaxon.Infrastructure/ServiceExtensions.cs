using Klaxon.Infrastructure.BackgroundServices;
using Klaxon.Infrastructure.Notifications;
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

        // The escalation engine polls Postgres for due escalations and advances them (ADR-001).
        services.AddHostedService<EscalationEngine>();

        // The engine writes pages to the outbox; the dispatcher delivers them (ADR-003).
        services.AddSingleton<INotificationChannel, LogChannel>();
        services.AddHostedService<NotificationDispatcher>();

        return services;
    }
}
