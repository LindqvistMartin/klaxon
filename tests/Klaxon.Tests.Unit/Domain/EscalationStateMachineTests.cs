using Klaxon.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class EscalationStateMachineTests
{
    private static readonly (EscalationState, EscalationState)[] LegalPairs =
    [
        (EscalationState.Triggered, EscalationState.Notified),
        (EscalationState.Triggered, EscalationState.Acked),
        (EscalationState.Triggered, EscalationState.Resolved),
        (EscalationState.Triggered, EscalationState.Exhausted),
        (EscalationState.Notified, EscalationState.Acked),
        (EscalationState.Notified, EscalationState.Resolved),
        (EscalationState.Notified, EscalationState.Exhausted),
        (EscalationState.Acked, EscalationState.Resolved),
    ];

    [Fact]
    public void AllowedTransitions_ContainsExactlyTheLegalPairs()
    {
        EscalationStateMachine.AllowedTransitions.Should().BeEquivalentTo(LegalPairs);
    }

    [Theory]
    [InlineData(EscalationState.Triggered, EscalationState.Notified)]
    [InlineData(EscalationState.Notified, EscalationState.Acked)]
    [InlineData(EscalationState.Notified, EscalationState.Exhausted)]
    [InlineData(EscalationState.Acked, EscalationState.Resolved)]
    public void IsAllowed_LegalPair_ReturnsTrue(EscalationState from, EscalationState to)
    {
        EscalationStateMachine.IsAllowed(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(EscalationState.Resolved, EscalationState.Notified)]
    [InlineData(EscalationState.Exhausted, EscalationState.Resolved)]
    [InlineData(EscalationState.Acked, EscalationState.Exhausted)]
    [InlineData(EscalationState.Acked, EscalationState.Notified)]
    [InlineData(EscalationState.Resolved, EscalationState.Resolved)]
    public void IsAllowed_IllegalPair_ReturnsFalse(EscalationState from, EscalationState to)
    {
        EscalationStateMachine.IsAllowed(from, to).Should().BeFalse();
    }
}
