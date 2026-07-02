using Klaxon.Core.Entities;
using FluentAssertions;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class EscalationTests
{
    private static readonly Instant Timeout = Instant.FromUtc(2026, 7, 2, 9, 5);

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
}
