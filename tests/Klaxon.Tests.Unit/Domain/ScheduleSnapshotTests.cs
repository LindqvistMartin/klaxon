using Klaxon.Core.Entities;
using Klaxon.Core.OnCall;
using FluentAssertions;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class ScheduleSnapshotTests
{
    private static readonly DateTimeZone Berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];

    private static User Person() => new(Guid.NewGuid(), "A", "a@example.com", "Europe/Berlin");

    [Fact]
    public void OverrideWindow_EndBeforeStart_Throws()
    {
        var act = () => new OverrideWindow(Person(), Instant.FromUtc(2026, 1, 2, 0, 0), Instant.FromUtc(2026, 1, 1, 0, 0));
        act.Should().Throw<ArgumentException>().WithParameterName("endsAt");
    }

    [Fact]
    public void OverrideWindow_EqualStartAndEnd_Throws()
    {
        var instant = Instant.FromUtc(2026, 1, 1, 0, 0);
        var act = () => new OverrideWindow(Person(), instant, instant);
        act.Should().Throw<ArgumentException>().WithParameterName("endsAt");
    }

    [Fact]
    public void OverrideWindow_NullUser_Throws()
    {
        var act = () => new OverrideWindow(null!, Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0));
        act.Should().Throw<ArgumentNullException>().WithParameterName("user");
    }

    [Fact]
    public void Snapshot_NullZone_Throws()
    {
        var act = () => new ScheduleSnapshot(RotationType.Daily, new LocalTime(9, 0), null!,
            new LocalDate(2026, 1, 1), [Person()], []);
        act.Should().Throw<ArgumentNullException>().WithParameterName("zone");
    }

    [Fact]
    public void Snapshot_EmptyParticipants_Allowed()
    {
        var snapshot = new ScheduleSnapshot(RotationType.Daily, new LocalTime(9, 0), Berlin,
            new LocalDate(2026, 1, 1), [], []);

        snapshot.Participants.Should().BeEmpty();
    }
}
