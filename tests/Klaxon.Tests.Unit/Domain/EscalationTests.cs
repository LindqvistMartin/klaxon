using Klaxon.Core.Entities;
using FluentAssertions;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class EscalationTests
{
    private static readonly Instant Timeout = Instant.FromUtc(2026, 7, 2, 9, 5);

    private static readonly Instant T0 = Instant.FromUtc(2026, 7, 2, 9, 0);
    private static readonly Instant T1 = Instant.FromUtc(2026, 7, 2, 9, 5);
    private static readonly Instant T2 = Instant.FromUtc(2026, 7, 2, 9, 10);

    private static Escalation Triggered() => new(Guid.NewGuid(), Guid.NewGuid(), T0);
    private static Escalation Notified() { var e = Triggered(); e.Advance(T1); return e; }
    private static Escalation Acked() { var e = Notified(); e.Ack(Guid.NewGuid(), T2); return e; }
    private static Escalation Resolved() { var e = Notified(); e.Resolve(T2); return e; }
    private static Escalation Exhausted() { var e = Notified(); e.Advance(null); return e; }

    [Fact]
    public void Constructor_StartsTriggeredAtLevelZero()
    {
        var escalation = new Escalation(Guid.NewGuid(), Guid.NewGuid(), Timeout);

        escalation.State.Should().Be(EscalationState.Triggered);
        escalation.CurrentLevel.Should().Be(0);
        escalation.NextTimeoutAt.Should().Be(Timeout);
        escalation.LeaseUntil.Should().BeNull();
        escalation.AckedBy.Should().BeNull();
        escalation.AckedAt.Should().BeNull();
        escalation.ResolvedAt.Should().BeNull();
        escalation.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Constructor_EmptyAlertId_Throws()
    {
        var act = () => new Escalation(Guid.Empty, Guid.NewGuid(), Timeout);
        act.Should().Throw<ArgumentException>().WithParameterName("alertId");
    }

    [Fact]
    public void Constructor_EmptyPolicyId_Throws()
    {
        var act = () => new Escalation(Guid.NewGuid(), Guid.Empty, Timeout);
        act.Should().Throw<ArgumentException>().WithParameterName("policyId");
    }

    [Fact]
    public void Advance_FromTriggered_MovesToNotifiedSameLevel()
    {
        var escalation = Triggered();

        escalation.Advance(T1);

        escalation.State.Should().Be(EscalationState.Notified);
        escalation.CurrentLevel.Should().Be(0);
        escalation.NextTimeoutAt.Should().Be(T1);
    }

    [Fact]
    public void Advance_FromNotified_IncrementsLevelStaysNotified()
    {
        var escalation = Notified();

        escalation.Advance(T2);

        escalation.State.Should().Be(EscalationState.Notified);
        escalation.CurrentLevel.Should().Be(1);
        escalation.NextTimeoutAt.Should().Be(T2);
    }

    [Fact]
    public void Advance_MultiLevelWalk_TracksCurrentLevel()
    {
        var escalation = Triggered();

        escalation.Advance(T1); // level 0 paged
        escalation.Advance(T2); // level 1
        escalation.Advance(T2); // level 2

        escalation.State.Should().Be(EscalationState.Notified);
        escalation.CurrentLevel.Should().Be(2);
    }

    [Fact]
    public void Advance_NullTimeout_FromNotified_Exhausts()
    {
        var escalation = Notified();

        escalation.Advance(null);

        escalation.State.Should().Be(EscalationState.Exhausted);
        escalation.NextTimeoutAt.Should().BeNull();
        escalation.CurrentLevel.Should().Be(0);
    }

    [Fact]
    public void Advance_NullTimeout_FromTriggered_Exhausts()
    {
        var escalation = Triggered();

        escalation.Advance(null);

        escalation.State.Should().Be(EscalationState.Exhausted);
        escalation.NextTimeoutAt.Should().BeNull();
    }

    [Fact]
    public void Advance_FromAcked_Throws()
    {
        var escalation = Acked();

        var act = () => escalation.Advance(T2);

        act.Should().Throw<InvalidOperationException>();
        escalation.State.Should().Be(EscalationState.Acked);
    }

    [Fact]
    public void Advance_FromResolved_Throws()
    {
        var escalation = Resolved();

        var act = () => escalation.Advance(T2);

        act.Should().Throw<InvalidOperationException>();
        escalation.State.Should().Be(EscalationState.Resolved);
    }

    [Fact]
    public void Advance_FromExhausted_Throws()
    {
        var escalation = Exhausted();

        var act = () => escalation.Advance(T2);

        act.Should().Throw<InvalidOperationException>();
        escalation.State.Should().Be(EscalationState.Exhausted);
    }

    [Fact]
    public void Ack_FromTriggered_SetsAckedAndStopsClock()
    {
        var escalation = Triggered();
        var actor = Guid.NewGuid();

        escalation.Ack(actor, T2);

        escalation.State.Should().Be(EscalationState.Acked);
        escalation.AckedBy.Should().Be(actor);
        escalation.AckedAt.Should().Be(T2);
        escalation.NextTimeoutAt.Should().BeNull();
    }

    [Fact]
    public void Ack_FromNotified_SetsAcked()
    {
        var escalation = Notified();
        var actor = Guid.NewGuid();

        escalation.Ack(actor, T2);

        escalation.State.Should().Be(EscalationState.Acked);
        escalation.AckedBy.Should().Be(actor);
    }

    [Fact]
    public void Ack_Repeat_IsNoOp_FirstAckerWins()
    {
        var escalation = Notified();
        var first = Guid.NewGuid();
        escalation.Ack(first, T1);

        escalation.Ack(Guid.NewGuid(), T2);

        escalation.AckedBy.Should().Be(first);
        escalation.AckedAt.Should().Be(T1);
    }

    [Fact]
    public void Ack_AfterResolve_IsNoOp()
    {
        var escalation = Resolved();

        escalation.Ack(Guid.NewGuid(), T2);

        escalation.State.Should().Be(EscalationState.Resolved);
        escalation.AckedBy.Should().BeNull();
    }

    [Fact]
    public void Ack_AfterExhausted_IsNoOp()
    {
        var escalation = Exhausted();

        escalation.Ack(Guid.NewGuid(), T2);

        escalation.State.Should().Be(EscalationState.Exhausted);
        escalation.AckedBy.Should().BeNull();
    }

    [Fact]
    public void Ack_EmptyActor_Throws()
    {
        var escalation = Notified();

        var act = () => escalation.Ack(Guid.Empty, T2);

        act.Should().Throw<ArgumentException>().WithParameterName("actor");
    }

    [Theory]
    [InlineData(0)] // Triggered
    [InlineData(1)] // Notified
    [InlineData(2)] // Acked
    public void Resolve_FromOpenState_SetsResolved(int stateStep)
    {
        var escalation = stateStep switch
        {
            0 => Triggered(),
            1 => Notified(),
            _ => Acked(),
        };

        escalation.Resolve(T2);

        escalation.State.Should().Be(EscalationState.Resolved);
        escalation.ResolvedAt.Should().Be(T2);
        escalation.NextTimeoutAt.Should().BeNull();
    }

    [Fact]
    public void Resolve_Repeat_IsNoOp()
    {
        var escalation = Resolved(); // ResolvedAt == T2

        escalation.Resolve(T0);

        escalation.ResolvedAt.Should().Be(T2);
    }

    [Fact]
    public void Resolve_AfterExhausted_IsNoOp()
    {
        var escalation = Exhausted();

        escalation.Resolve(T2);

        escalation.State.Should().Be(EscalationState.Exhausted);
        escalation.ResolvedAt.Should().BeNull();
    }
}
