using System.Net;
using Klaxon.Tests.Integration.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Klaxon.Tests.Integration.Endpoints;

[Collection("Api")]
public sealed class ReadinessProbeTests(ApiFactory factory)
{
    [Fact]
    public async Task GetReady_WithPostgresUp_Returns200()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz/ready");

        // Readiness runs the PostgresHealthCheck against the live container, so a green probe also
        // proves the harness actually booted the host against a reachable database.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
