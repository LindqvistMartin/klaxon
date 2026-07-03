using Klaxon.Core.Entities;
using Klaxon.Core.OnCall;
using FluentAssertions;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class OnCallResolverTests
{
    private static readonly DateTimeZone Berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];
    private static readonly DateTimeZone London = DateTimeZoneProviders.Tzdb["Europe/London"];
    private static readonly LocalTime NineAm = new(9, 0);

    private static User Person(string name) =>
        new(Guid.NewGuid(), name, name + "@example.com", "Europe/Berlin");

    private static ScheduleSnapshot Snap(
        IReadOnlyList<User> participants,
        RotationType rotation = RotationType.Daily,
        LocalTime? handoff = null,
        DateTimeZone? zone = null,
        LocalDate? anchor = null,
        IReadOnlyList<OverrideWindow>? overrides = null) =>
        new(rotation, handoff ?? NineAm, zone ?? Berlin,
            anchor ?? new LocalDate(2026, 1, 1), participants, overrides ?? []);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    public void WhoIsOnCall_DailyRotation_MapsDayOffsetToParticipant(int dayOffset, int expectedIndex)
    {
        var people = new[] { Person("A"), Person("B"), Person("C") };
        var snap = Snap(people, RotationType.Daily, anchor: new LocalDate(2026, 1, 1));
        // Mid-shift at noon Berlin; January has no DST change so each step is a clean 24h.
        var at = Instant.FromUtc(2026, 1, 1, 11, 0).Plus(Duration.FromDays(dayOffset));

        OnCallResolver.WhoIsOnCall(snap, at).Should().Be(people[expectedIndex]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 1)]
    [InlineData(14, 2)]
    [InlineData(21, 0)]
    public void WhoIsOnCall_WeeklyRotation_AdvancesEverySevenDays(int dayOffset, int expectedIndex)
    {
        var people = new[] { Person("A"), Person("B"), Person("C") };
        var snap = Snap(people, RotationType.Weekly, anchor: new LocalDate(2026, 1, 1));
        var at = Instant.FromUtc(2026, 1, 1, 11, 0).Plus(Duration.FromDays(dayOffset));

        OnCallResolver.WhoIsOnCall(snap, at).Should().Be(people[expectedIndex]);
    }

    [Fact]
    public void WhoIsOnCall_SingleParticipant_AlwaysReturnsThatUser()
    {
        var solo = Person("Solo");
        var snap = Snap([solo], RotationType.Daily, anchor: new LocalDate(2026, 1, 1));

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 1, 11, 0)).Should().Be(solo);
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 3, 30, 11, 0)).Should().Be(solo);
    }

    [Fact]
    public void WhoIsOnCall_BeforeFirstHandoffOnAnchorDate_ReturnsNull()
    {
        var people = new[] { Person("A"), Person("B") };
        var snap = Snap(people, handoff: NineAm, anchor: new LocalDate(2026, 1, 1));

        // 08:00 Berlin (07:00Z) is before the 09:00 handoff; 09:00 Berlin (08:00Z) is exactly it.
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 1, 7, 0)).Should().BeNull();
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 1, 8, 0)).Should().Be(people[0]);
    }

    [Fact]
    public void WhoIsOnCall_BeforeAnchor_ReturnsNull()
    {
        var people = new[] { Person("A"), Person("B") };
        var snap = Snap(people, anchor: new LocalDate(2026, 1, 10));

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 5, 11, 0)).Should().BeNull();
    }

    [Fact]
    public void WhoIsOnCall_EmptyParticipants_ReturnsNull()
    {
        var snap = Snap([], anchor: new LocalDate(2026, 1, 1));

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 5, 11, 0)).Should().BeNull();
    }

    [Fact]
    public void WhoIsOnCall_SpringForwardGapHandoff_UsesFirstInstantAfterGap()
    {
        var people = new[] { Person("A"), Person("B") };
        // Handoff to B nominally at 02:30 on 2026-03-29 lands in Berlin's spring-forward gap
        // (02:00->03:00 never occurs), so it fires at 03:00 Berlin = 01:00Z.
        var snap = Snap(people, RotationType.Daily, handoff: new LocalTime(2, 30), anchor: new LocalDate(2026, 3, 28));

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 3, 29, 0, 30)).Should().Be(people[0]); // before -> A
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 3, 29, 1, 0)).Should().Be(people[1]);  // at gap end -> B
    }

    [Fact]
    public void WhoIsOnCall_FallBackOverlapHandoff_UsesLaterInstantNoDoubleHandoff()
    {
        var people = new[] { Person("A"), Person("B") };
        // 02:30 on 2026-10-25 occurs twice in Berlin's fall-back overlap. The handoff fires at the
        // later instant (01:30Z), so the first 02:30 pass (00:45Z) is still the outgoing engineer.
        var snap = Snap(people, RotationType.Daily, handoff: new LocalTime(2, 30), anchor: new LocalDate(2026, 10, 24));

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 10, 25, 0, 45)).Should().Be(people[0]);
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 10, 25, 1, 30)).Should().Be(people[1]);
    }

    [Fact]
    public void WhoIsOnCall_DstChangeMidShift_KeepsSameParticipant()
    {
        var people = new[] { Person("A"), Person("B") };
        // Weekly shift 0 runs 2026-03-25..04-01; the spring-forward change on 03-29 is mid-shift.
        var snap = Snap(people, RotationType.Weekly, anchor: new LocalDate(2026, 3, 25));

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 3, 27, 11, 0)).Should().Be(people[0]); // before DST
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 3, 30, 10, 0)).Should().Be(people[0]); // after DST
    }

    [Fact]
    public void WhoIsOnCall_LondonZone_ResolvesInScheduleTimezone()
    {
        var people = new[] { Person("A"), Person("B") };
        var snap = Snap(people, RotationType.Daily, handoff: NineAm, zone: London, anchor: new LocalDate(2026, 1, 1));

        // 2026-01-02 12:00 London (UTC+0 in winter) -> day offset 1 -> B.
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 2, 12, 0)).Should().Be(people[1]);
    }

    [Fact]
    public void WhoIsOnCall_OverrideCoversInstant_BeatsRotation()
    {
        var rotation = new[] { Person("A"), Person("B") };
        var cover = Person("Cover");
        var window = new OverrideWindow(cover, Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0));
        var snap = Snap(rotation, anchor: new LocalDate(2026, 1, 1), overrides: [window]);

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 1, 11, 0)).Should().Be(cover);
    }

    [Fact]
    public void WhoIsOnCall_OverlappingOverrides_LatestStartWins()
    {
        var early = Person("Early");
        var late = Person("Late");
        var first = new OverrideWindow(early, Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 3, 0, 0));
        var second = new OverrideWindow(late, Instant.FromUtc(2026, 1, 2, 0, 0), Instant.FromUtc(2026, 1, 4, 0, 0));
        var snap = Snap([Person("A")], overrides: [first, second]);

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 2, 12, 0)).Should().Be(late);
    }

    [Fact]
    public void WhoIsOnCall_OverrideIsHalfOpen()
    {
        var rotation = new[] { Person("A"), Person("B") };
        var cover = Person("Cover");
        var start = Instant.FromUtc(2026, 1, 2, 8, 0);  // 09:00 Berlin, the day-1 rotation boundary (B)
        var end = Instant.FromUtc(2026, 1, 2, 12, 0);
        var window = new OverrideWindow(cover, start, end);
        var snap = Snap(rotation, anchor: new LocalDate(2026, 1, 1), overrides: [window]);

        OnCallResolver.WhoIsOnCall(snap, start).Should().Be(cover);        // start is inclusive
        OnCallResolver.WhoIsOnCall(snap, end).Should().Be(rotation[1]);    // end is exclusive -> rotation
    }

    [Fact]
    public void WhoIsOnCall_OutsideOverrideWithNoParticipants_ReturnsNull()
    {
        var cover = Person("Cover");
        var window = new OverrideWindow(cover, Instant.FromUtc(2026, 1, 2, 0, 0), Instant.FromUtc(2026, 1, 3, 0, 0));
        var snap = Snap([], overrides: [window]);

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 2, 12, 0)).Should().Be(cover); // inside window
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 5, 12, 0)).Should().BeNull();   // gap, no rotation
    }

    [Fact]
    public void WhoIsOnCall_OverrideDuringShift_RotationResumesAtCorrectIndex()
    {
        var people = new[] { Person("P0"), Person("P1"), Person("P2") };
        var cover = Person("Cover");
        // Override covers only the day-1 shift; the rotation must resume at P2 on day 2, not restart.
        var window = new OverrideWindow(cover, Instant.FromUtc(2026, 1, 2, 0, 0), Instant.FromUtc(2026, 1, 3, 0, 0));
        var snap = Snap(people, RotationType.Daily, anchor: new LocalDate(2026, 1, 1), overrides: [window]);

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 1, 11, 0)).Should().Be(people[0]); // before
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 2, 12, 0)).Should().Be(cover);      // during
        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 3, 11, 0)).Should().Be(people[2]);  // after
    }

    [Fact]
    public void WhoIsOnCall_OverridesWithEqualStart_LongerWindowWins_OrderIndependent()
    {
        var shortCover = Person("Short");
        var longCover = Person("Long");
        var start = Instant.FromUtc(2026, 1, 1, 0, 0);
        var shortWindow = new OverrideWindow(shortCover, start, Instant.FromUtc(2026, 1, 2, 0, 0));
        var longWindow = new OverrideWindow(longCover, start, Instant.FromUtc(2026, 1, 3, 0, 0));
        var at = Instant.FromUtc(2026, 1, 1, 12, 0);

        OnCallResolver.WhoIsOnCall(Snap([Person("A")], overrides: [shortWindow, longWindow]), at).Should().Be(longCover);
        OnCallResolver.WhoIsOnCall(Snap([Person("A")], overrides: [longWindow, shortWindow]), at).Should().Be(longCover);
    }

    [Fact]
    public void WhoIsOnCall_WeeklyBeforeAnchor_ReturnsNull()
    {
        var people = new[] { Person("A"), Person("B") };
        var snap = Snap(people, RotationType.Weekly, anchor: new LocalDate(2026, 1, 15));

        OnCallResolver.WhoIsOnCall(snap, Instant.FromUtc(2026, 1, 10, 11, 0)).Should().BeNull();
    }
}
