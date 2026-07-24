using Klaxon.Infrastructure.Ack;
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

        // Signs and verifies the no-login ack links a page carries (ADR-007). Bound by hand off the
        // IConfiguration in the container so AddInfrastructure keeps its parameterless signature.
        services.AddOptions<AckOptions>().Configure<IConfiguration>((ack, configuration) =>
        {
            var section = configuration.GetSection("Ack");
            ack.SigningKey = section["SigningKey"] ?? "";
            ack.LinkBaseUrl = section["LinkBaseUrl"] ?? "";
            if (TimeSpan.TryParse(section["LinkLifetime"], out var lifetime))
                ack.LinkLifetime = lifetime;
        });
        services.AddSingleton<IAckTokenService, AckTokenService>();
        services.AddSingleton<IAckLinkFactory, AckLinkFactory>();

        return services;
    }
}
