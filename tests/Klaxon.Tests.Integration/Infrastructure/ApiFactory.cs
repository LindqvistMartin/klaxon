using Klaxon.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    }

    public async Task CleanAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        // One statement; CASCADE chases every FK dependent, so ordering does not matter. Add a
        // table here when a new entity lands — the list is not derived from EF metadata.
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE TABLE "Escalations", "Alerts", "EscalationLevels", "EscalationPolicies", "ScheduleOverrides", "Schedules", "Users", "Teams", "Organizations" CASCADE""");
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
