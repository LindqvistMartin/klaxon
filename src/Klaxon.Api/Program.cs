var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "Klaxon API");

// Liveness: the process is up. Deliberately dependency-free — readiness (Postgres
// reachability, escalation-engine liveness, outbox lag) arrives with those subsystems.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so the integration test project can boot the app via WebApplicationFactory<Program>.
public partial class Program;
