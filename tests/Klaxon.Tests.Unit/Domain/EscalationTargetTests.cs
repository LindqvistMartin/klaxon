using Klaxon.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class EscalationTargetTests
{
    [Fact]
    public void Constructor_BlankReference_Throws()
    {
        var act = () => new EscalationTarget(EscalationTargetKind.User, "  ");
        act.Should().Throw<ArgumentException>().WithParameterName("reference");
    }

    [Fact]
    public void Equality_SameKindAndReference_AreEqual()
    {
        var reference = Guid.NewGuid().ToString();
        var a = new EscalationTarget(EscalationTargetKind.Schedule, reference);
        var b = new EscalationTarget(EscalationTargetKind.Schedule, reference);

        a.Should().Be(b);
    }

    [Fact]
    public void Equality_DifferentKind_AreNotEqual()
    {
        var reference = Guid.NewGuid().ToString();
        var a = new EscalationTarget(EscalationTargetKind.Schedule, reference);
        var b = new EscalationTarget(EscalationTargetKind.User, reference);

        a.Should().NotBe(b);
    }
}
