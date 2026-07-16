using System.Text.Json.Serialization;
using Klaxon.Api.Endpoints;
using Klaxon.Api.Errors;
using Klaxon.Api.HealthChecks;
using Klaxon.Infrastructure;
using Klaxon.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Scalar.AspNetCore;
using Serilog;

// Bootstrap logger: captures failures during host construction (before the real logger is built).
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Postgres")))
        throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.Services.AddInfrastructure();

    // NodaTime types (Instant, LocalTime) and string enums cross the wire; without this the JSON is
    // malformed and enum values would not match the string columns they are stored in. Enforcing the
    // DTO nullability/required annotations turns a missing or null required field into a 400 at
    // binding time instead of a null that surfaces as a 500 deeper in a handler.
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.SerializerOptions.RespectNullableAnnotations = true;
        options.SerializerOptions.RespectRequiredConstructorParameters = true;
    });

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<DomainExceptionHandler>();

    // Route body-binding failures (malformed JSON, a missing required member) through the exception
    // handler in every environment. ThrowOnBadRequest otherwise defaults to IsDevelopment, so in
    // production a bad body would short-circuit to a bare 400; this makes it the same problem-details
    // 400 the rest of the API emits, and keeps DomainExceptionHandler's BadHttpRequestException arm live.
    builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

    builder.Services.AddHealthChecks()
        .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

    builder.Services.AddOpenApi();

    var app = builder.Build();

    // Request logging first, so the access log records the final (translated) status code.
    app.UseSerilogRequestLogging();
    // Before the endpoints; relies on AddProblemDetails to write the response body.
    app.UseExceptionHandler();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        await db.Database.MigrateAsync();
    }

    app.MapOpenApi();
    app.MapScalarApiReference();

    app.MapGet("/", () => "Klaxon API");

    // Liveness: the process is up. Deliberately dependency-free.
    app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

    // Readiness: dependencies the app needs to serve traffic. Postgres today; the escalation
    // engine's liveness joins the same "ready" tag once it exists.
    app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
    });

    var v1 = app.MapGroup("/api/v1");
    v1.MapScheduleEndpoints();
    v1.MapScheduleOverrideEndpoints();
    v1.MapEscalationPolicyEndpoints();
    v1.MapAlertEndpoints();

    app.Run();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    Log.Fatal(exception, "Klaxon terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

// Exposed so the integration test project can boot the app via WebApplicationFactory<Program>.
public partial class Program;
