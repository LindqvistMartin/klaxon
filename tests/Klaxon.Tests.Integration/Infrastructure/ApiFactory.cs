using Klaxon.Infrastructure.BackgroundServices;
using Klaxon.Infrastructure.Notifications;
using Klaxon.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace Klaxon.Tests.Integration.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("klaxon_test")
        .Build();

    private bool _disposed;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        try
        {
            // Force the lazy host build, then migrate explicitly. Program.cs runs MigrateAsync before
            // app.Run() (which is where WAF short-circuits), so under the normal path it has already
            // run by the time Server is built — this explicit call is belt-and-suspenders that keeps
            // the harness working if that startup migration is ever moved out of Program.cs.
            _ = Server;
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
            await db.Database.MigrateAsync();
        }
        catch
        {
            // A host-build or migration failure must not strand a Postgres container per failed
            // run in CI.
            await _pg.DisposeAsync();
            throw;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Appended after Program.cs' own configuration sources, so this connection string wins and
        // points the app at the throwaway container instead of any developer-local Postgres.
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _pg.GetConnectionString(),
            });
        });

        // Strip both hosted pollers so neither races test state, and register each as a plain
        // singleton so the tests can resolve it and drive one deterministic tick at a time. The
        // dispatcher matters as much as the engine here: left running, it would drain the outbox
        // out from under the engine tests that assert what was written.
        builder.ConfigureTestServices(services =>
        {
            RemoveHosted<EscalationEngine>(services);
            services.AddSingleton<EscalationEngine>();

            RemoveHosted<NotificationDispatcher>(services);
            services.AddSingleton<NotificationDispatcher>();

            // Swap the log channel for one the tests can read back and fail on demand.
            services.RemoveAll<INotificationChannel>();
            services.AddSingleton<RecordingChannel>();
            services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<RecordingChannel>());
        });
    }

    // Removes the AddHostedService registration for T by matching its ImplementationType, so the
    // engine can be re-added as a plain singleton without also ticking on the poll loop.
    private static void RemoveHosted<T>(IServiceCollection services) where T : IHostedService
    {
        var matches = services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T))
            .ToList();
        foreach (var descriptor in matches)
            services.Remove(descriptor);
    }

    // The one clean-slate call: a test class that only knows about this still gets an empty channel,
    // so a sticky ThrowOnSend or a stale Sent list cannot leak into it.
    public async Task CleanAsync()
    {
        Services.GetRequiredService<RecordingChannel>().Reset();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        // One statement; CASCADE chases every FK dependent, so ordering does not matter. Add a
        // table here when a new entity lands — the list is not derived from EF metadata.
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE TABLE "Escalations", "Alerts", "EscalationLevels", "EscalationPolicies", "ScheduleOverrides", "Schedules", "Users", "Teams", "Organizations", "OutboxMessages" CASCADE""");
    }

    public override async ValueTask DisposeAsync()
    {
        // xUnit's IAsyncLifetime.DisposeAsync and `await using` both land here; base.DisposeAsync
        // is not documented as idempotent across versions, so guard against the second pass.
        if (_disposed)
            return;
        _disposed = true;
        // Tear the host down before the container so Npgsql connections close cleanly; the reverse
        // order leaks connections and produces noisy teardown errors.
        await base.DisposeAsync();
        await _pg.DisposeAsync();
    }

    // xUnit 2.x IAsyncLifetime.DisposeAsync returns Task; WebApplicationFactory.DisposeAsync
    // returns ValueTask. Bridge the two so both teardown paths run the same logic.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();
}
