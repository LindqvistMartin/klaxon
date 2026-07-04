using System.Net;
using System.Net.Http.Json;
using Klaxon.Api.Contracts;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Persistence;
using Klaxon.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Integration.Endpoints;

[Collection("Api")]
public sealed class ScheduleCrudTests(ApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.CleanAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PostSchedule_ThenGetById_RoundTripsParticipantsAndTime()
    {
        var teamId = await SeedTeamAsync();
        var client = factory.CreateClient();

        var participants = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var request = new CreateScheduleRequest(teamId, "Primary", RotationType.Weekly,
            new LocalTime(9, 0), "Europe/Berlin", participants);

        var createResponse = await client.PostAsJsonAsync("/api/v1/schedules", request, TestJson.Options);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await createResponse.Content.ReadFromJsonAsync<ScheduleResponse>(TestJson.Options))!;
        created.ParticipantOrder.Should().Equal(participants);
        created.HandoffTime.Should().Be(new LocalTime(9, 0));

        var getResponse = await client.GetAsync($"/api/v1/schedules/{created.Id}");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = (await getResponse.Content.ReadFromJsonAsync<ScheduleResponse>(TestJson.Options))!;
        // The jsonb ParticipantOrder and the NodaTime columns survive a full write then reload
        // through Postgres, not just the in-memory value converter.
        fetched.ParticipantOrder.Should().Equal(participants);
        fetched.HandoffTime.Should().Be(new LocalTime(9, 0));
        fetched.RotationType.Should().Be(RotationType.Weekly);
        fetched.TimeZoneId.Should().Be("Europe/Berlin");
        // Not asserted for exact equality: Instant carries nanoseconds but a Postgres timestamptz
        // round-trip truncates to microseconds. A greater-than-epoch check still guards against a
        // regression that drops CreatedAt to its default instead of materializing the stored value.
        fetched.CreatedAt.Should().BeGreaterThan(NodaConstants.UnixEpoch);
    }

    [Fact]
    public async Task PostSchedule_UnknownTeam_Returns409()
    {
        var client = factory.CreateClient();

        var request = new CreateScheduleRequest(Guid.NewGuid(), "Primary", RotationType.Daily,
            new LocalTime(8, 0), "Europe/Berlin", [Guid.NewGuid()]);

        var response = await client.PostAsJsonAsync("/api/v1/schedules", request, TestJson.Options);

        // The TeamId FK has no matching row, so Postgres raises 23503, which DomainExceptionHandler
        // maps to 409 rather than letting a raw DbUpdateException surface as a 500. Asserting the
        // title pins the response to that FK arm, not just any 409.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);
        problem!.Title.Should().Be("A referenced resource does not exist.");
    }

    [Fact]
    public async Task PostSchedule_BlankName_Returns400()
    {
        var teamId = await SeedTeamAsync();
        var client = factory.CreateClient();

        var request = new CreateScheduleRequest(teamId, "  ", RotationType.Daily,
            new LocalTime(8, 0), "Europe/Berlin", [Guid.NewGuid()]);

        var response = await client.PostAsJsonAsync("/api/v1/schedules", request, TestJson.Options);

        // A blank name trips the Schedule constructor's ArgumentException guard, which
        // DomainExceptionHandler maps to a 400 — the domain-is-the-validation-layer path, distinct
        // from the framework's body-binding 400.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);
        problem!.Title.Should().Be("One or more validation errors occurred.");
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
