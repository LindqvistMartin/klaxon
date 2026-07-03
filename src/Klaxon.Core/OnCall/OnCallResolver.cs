using Klaxon.Core.Entities;
using NodaTime;
using NodaTime.TimeZones;

namespace Klaxon.Core.OnCall;

// Answers "who is on call at instant T?" as a pure function (ADR-002). Rotation boundaries are
// computed in the schedule's own timezone, overrides beat the computed rotation, and DST is
// resolved explicitly instead of by accident.
public static class OnCallResolver
{
    // The recorded DST choice. A handoff time that never occurs (spring-forward gap) fires at the
    // first instant after the gap; a handoff time that occurs twice (fall-back overlap) fires at
    // the later instant, so the outgoing engineer keeps the ambiguous hour and no one is paged
    // twice. This is NodaTime's lenient behaviour, pinned here so the choice is deliberate.
    private static readonly ZoneLocalMappingResolver HandoffResolver =
        Resolvers.CreateMappingResolver(Resolvers.ReturnLater, Resolvers.ReturnStartOfIntervalAfter);

    public static User? WhoIsOnCall(ScheduleSnapshot snapshot, Instant at)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Overrides win over the computed rotation. On overlap the latest-starting window wins,
        // with the latest-ending one as a deterministic tie-break.
        OverrideWindow? winner = null;
        foreach (var window in snapshot.Overrides)
        {
            if (window.StartsAt > at || at >= window.EndsAt)
                continue;
            if (winner is null
                || window.StartsAt > winner.StartsAt
                || (window.StartsAt == winner.StartsAt && window.EndsAt > winner.EndsAt))
            {
                winner = window;
            }
        }

        if (winner is not null)
            return winner.User;

        if (snapshot.Participants.Count == 0)
            return null;

        int periodDays = snapshot.Rotation == RotationType.Daily ? 1 : 7;

        // Index the rotation by whole days from the anchor, then apply a single bounded correction
        // for the two ways the date-based index can overshoot the true instant-based one: 'at'
        // sitting before the handoff time on a boundary date, or a DST shift nudging the boundary
        // instant. Both can only lower the index by one, never raise it, so one check is exact.
        LocalDate onDate = at.InZone(snapshot.Zone).Date;
        int dayOffset = Period.Between(snapshot.RotationAnchor, onDate, PeriodUnits.Days).Days;
        int index = FloorDiv(dayOffset, periodDays);
        if (at < ShiftStart(snapshot, index, periodDays))
            index -= 1;

        if (index < 0)
            return null; // 'at' precedes the first shift

        return snapshot.Participants[index % snapshot.Participants.Count];
    }

    private static Instant ShiftStart(ScheduleSnapshot snapshot, int index, int periodDays)
    {
        LocalDateTime handoff = snapshot.RotationAnchor.PlusDays(index * periodDays).At(snapshot.HandoffTime);
        return snapshot.Zone.ResolveLocal(handoff, HandoffResolver).ToInstant();
    }

    // Integer floor division. The divisor is always +1 or +7, so this only has to correct the
    // truncation-toward-zero behaviour of '/' for negative day offsets (instants before the anchor).
    private static int FloorDiv(int dividend, int divisor)
    {
        int quotient = dividend / divisor;
        if (dividend % divisor != 0 && (dividend < 0) != (divisor < 0))
            quotient--;
        return quotient;
    }
}
