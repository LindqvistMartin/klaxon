using Klaxon.Core.Entities;
using FluentAssertions;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class ScheduleTests
{
    private static readonly LocalTime Handoff = new(9, 0);

    private static Schedule Make(IReadOnlyList<Guid>? participants = null) =>
        new(Guid.NewGuid(), "Primary", RotationType.Weekly, Handoff, "Europe/Berlin",
            participants ?? [Guid.NewGuid(), Guid.NewGuid()]);

    [Fact]
    public void Constructor_Valid_PreservesParticipantOrder()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var schedule = Make([a, b, c]);

        schedule.ParticipantOrder.Should().Equal(a, b, c);
        schedule.TimeZoneId.Should().Be("Europe/Berlin");
        schedule.HandoffTime.Should().Be(Handoff);
    }

    [Fact]
    public void Constructor_InvalidTimeZone_Throws()
    {
        var act = () => new Schedule(Guid.NewGuid(), "Primary", RotationType.Daily, Handoff,
            "Mars/Olympus_Mons", [Guid.NewGuid()]);
        act.Should().Throw<ArgumentException>().WithParameterName("timeZoneId");
    }

    [Fact]
    public void Constructor_NoParticipants_Throws()
    {
        var act = () => Make([]);
        act.Should().Throw<ArgumentException>().WithParameterName("participantOrder");
    }

    [Fact]
    public void Constructor_EmptyParticipantId_Throws()
    {
        var act = () => Make([Guid.NewGuid(), Guid.Empty]);
        act.Should().Throw<ArgumentException>().WithParameterName("participantOrder");
    }

    [Fact]
    public void Constructor_BlankName_Throws()
    {
        var act = () => new Schedule(Guid.NewGuid(), "  ", RotationType.Daily, Handoff,
            "Europe/Berlin", [Guid.NewGuid()]);
        act.Should().Throw<ArgumentException>();
    }
}
