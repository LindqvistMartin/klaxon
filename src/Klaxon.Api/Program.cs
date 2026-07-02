using Klaxon.Infrastructure;
using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Postgres")))
    throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddInfrastructure();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
    await db.Database.MigrateAsync();
}

app.MapGet("/", () => "Klaxon API");

// Liveness: the process is up. Deliberately dependency-free — readiness (Postgres
// reachability, escalation-engine liveness, outbox lag) arrives with those subsystems.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so the integration test project can boot the app via WebApplicationFactory<Program>.
public partial class Program;
