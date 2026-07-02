using Klaxon.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class AlertTests
{
    [Fact]
    public void Constructor_StartsOpen()
    {
        var alert = new Alert("prometheus", "high-cpu:web-1", """{"severity":"critical"}""");

        alert.Status.Should().Be(AlertStatus.Open);
        alert.Source.Should().Be("prometheus");
        alert.DedupKey.Should().Be("high-cpu:web-1");
        alert.ResolvedAt.Should().BeNull();
        alert.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Constructor_BlankSource_Throws()
    {
        var act = () => new Alert(" ", "key", "{}");
        act.Should().Throw<ArgumentException>().WithParameterName("source");
    }

    [Fact]
    public void Constructor_BlankDedupKey_Throws()
    {
        var act = () => new Alert("prometheus", "", "{}");
        act.Should().Throw<ArgumentException>().WithParameterName("dedupKey");
    }
}
