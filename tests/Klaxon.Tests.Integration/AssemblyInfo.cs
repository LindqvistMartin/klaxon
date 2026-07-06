using Xunit;

// Integration tests share process-global state — the Serilog bootstrap logger and one Postgres
// container behind the "Api" collection fixture. A second WebApplicationFactory<Program> host
// building in parallel crashes on Serilog's already-frozen global logger, so serialize the whole
// assembly rather than relying on every future test class remembering [Collection("Api")].
[assembly: CollectionBehavior(DisableTestParallelization = true)]
