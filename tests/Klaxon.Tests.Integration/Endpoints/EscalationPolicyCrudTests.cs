using System.Net;
using System.Net.Http.Json;
using Klaxon.Api.Contracts;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Persistence;
using Klaxon.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Klaxon.Tests.Integration.Endpoints;

[Collection("Api")]
public sealed class EscalationPolicyCrudTests(ApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.CleanAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PostPolicy_ThenGetById_RoundTripsLevelsAndTargets()
    {
        var teamId = await SeedTeamAsync();
        var client = factory.CreateClient();

        var request = new CreateEscalationPolicyRequest(teamId, "Primary on-call",
        [
            new EscalationLevelDto(0, 300,
            [
                new EscalationTargetDto(EscalationTargetKind.Schedule, "primary-rotation"),
                new EscalationTargetDto(EscalationTargetKind.User, Guid.NewGuid().ToString()),
            ]),
            new EscalationLevelDto(1, 600,
            [
                new EscalationTargetDto(EscalationTargetKind.Channel, "slack:#incidents"),
            ]),
        ]);

        var createResponse = await client.PostAsJsonAsync("/api/v1/escalation-policies", request, TestJson.Options);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await createResponse.Content.ReadFromJsonAsync<EscalationPolicyResponse>(TestJson.Options))!;

        var getResponse = await client.GetAsync($"/api/v1/escalation-policies/{created.Id}");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = (await getResponse.Content.ReadFromJsonAsync<EscalationPolicyResponse>(TestJson.Options))!;
        // The nested Targets ride in a jsonb column; a reload from Postgres proves the structure
        // (levels ordered by Position, target Kind/Reference) survives the value converter.
        fetched.Levels.Should().HaveCount(2);
        fetched.Levels[0].Position.Should().Be(0);
        fetched.Levels[0].TimeoutSeconds.Should().Be(300);
        fetched.Levels[0].Targets.Should().BeEquivalentTo(request.Levels[0].Targets);
        fetched.Levels[1].Targets.Should().ContainSingle()
            .Which.Kind.Should().Be(EscalationTargetKind.Channel);

        // Lock the by-name enum storage the converter depends on (JsonbConverters): an accidental
        // switch to ordinal storage would round-trip identically above and pass, so read the raw
        // jsonb and assert the kinds are persisted as names, not integers.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        var targetsJson = await db.Database
            .SqlQueryRaw<string>("""SELECT "Targets"::text AS "Value" FROM "EscalationLevels" WHERE "Position" = 0""")
            .SingleAsync();
        targetsJson.Should().Contain("Schedule").And.Contain("User");
    }

    [Fact]
    public async Task PostPolicy_MissingLevels_Returns400()
    {
        var teamId = await SeedTeamAsync();
        var client = factory.CreateClient();

        // Levels is a required, non-nullable record parameter; omitting it makes
        // RespectRequiredConstructorParameters throw during binding, surfacing as a
        // BadHttpRequestException that the handler's arm turns into a problem-details 400 (with
        // ThrowOnBadRequest on) rather than a null-deref 500 in the endpoint's foreach.
        var response = await client.PostAsJsonAsync("/api/v1/escalation-policies",
            new { teamId, name = "No levels" }, TestJson.Options);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);
        problem!.Title.Should().Be("The request body is invalid.");
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        var org = new Organization("Test Org", "test-org");
        var team = new Team(org.Id, "Test Team", "test-team");
        db.Organizations.Add(org);
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return team.Id;
    }
}
