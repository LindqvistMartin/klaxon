using Klaxon.Core.Entities;
using FluentAssertions;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class AlertTests
{
    private static readonly Instant T0 = Instant.FromUtc(2026, 7, 16, 9, 0);
    private static readonly Instant T1 = Instant.FromUtc(2026, 7, 16, 9, 5);

    private static Alert Open() => new("prometheus", "high-cpu:web-1", """{"severity":"critical"}""");

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

    [Fact]
    public void Constructor_MalformedJsonPayload_Throws()
    {
        var act = () => new Alert("prometheus", "high-cpu:web-1", "not json");
        act.Should().Throw<ArgumentException>().WithParameterName("payload");
    }

    // Well-formed JSON that jsonb would store without complaint, and that carries nothing to page on.
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    public void Constructor_ScalarPayload_Throws(string payload)
    {
        var act = () => new Alert("prometheus", "high-cpu:web-1", payload);
        act.Should().Throw<ArgumentException>().WithParameterName("payload");
    }

    // Source and DedupKey arrive from an ingest URL and an untrusted webhook body, so a value
    // longer than its column is caller input, not a bug: guarding here turns a Postgres 22001 at
    // SaveChanges into the same 400 every other malformed field gets.
    [Fact]
    public void Constructor_SourceLongerThanColumn_Throws()
    {
        var act = () => new Alert(new string('s', 101), "key", "{}");
        act.Should().Throw<ArgumentException>().WithParameterName("source");
    }

    [Fact]
    public void Constructor_DedupKeyLongerThanColumn_Throws()
    {
        var act = () => new Alert("prometheus", new string('k', 201), "{}");
        act.Should().Throw<ArgumentException>().WithParameterName("dedupKey");
    }

    [Fact]
    public void Resolve_OpenAlert_SetsResolved()
    {
        var alert = Open();

        alert.Resolve(T1);

        alert.Status.Should().Be(AlertStatus.Resolved);
        alert.ResolvedAt.Should().Be(T1);
    }

    // Idempotent for the same reason Escalation.Resolve is: a monitoring source repeats its
    // resolved notification, and the second arrival must not rewrite when the incident ended.
    [Fact]
    public void Resolve_Repeat_IsNoOp()
    {
        var alert = Open();
        alert.Resolve(T1);

        alert.Resolve(T0);

        alert.ResolvedAt.Should().Be(T1);
    }
}
