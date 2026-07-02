using Klaxon.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class EscalationLevelTests
{
    private static IReadOnlyList<EscalationTarget> OneTarget() =>
        [new EscalationTarget(EscalationTargetKind.Schedule, Guid.NewGuid().ToString())];

    [Fact]
    public void Constructor_Valid_SetsProperties()
    {
        var policyId = Guid.NewGuid();
        var level = new EscalationLevel(policyId, position: 0, timeoutSeconds: 300, OneTarget());

        level.PolicyId.Should().Be(policyId);
        level.Position.Should().Be(0);
        level.TimeoutSeconds.Should().Be(300);
        level.Targets.Should().HaveCount(1);
    }

    [Fact]
    public void Constructor_NegativePosition_Throws()
    {
        var act = () => new EscalationLevel(Guid.NewGuid(), -1, 300, OneTarget());
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("position");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Constructor_NonPositiveTimeout_Throws(int timeoutSeconds)
    {
        var act = () => new EscalationLevel(Guid.NewGuid(), 0, timeoutSeconds, OneTarget());
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("timeoutSeconds");
    }

    [Fact]
    public void Constructor_NoTargets_Throws()
    {
        var act = () => new EscalationLevel(Guid.NewGuid(), 0, 300, []);
        act.Should().Throw<ArgumentException>().WithParameterName("targets");
    }

    [Fact]
    public void Constructor_EmptyPolicyId_Throws()
    {
        var act = () => new EscalationLevel(Guid.Empty, 0, 300, OneTarget());
        act.Should().Throw<ArgumentException>().WithParameterName("policyId");
    }
}
