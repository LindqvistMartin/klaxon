using System.Text.Json;
using Klaxon.Api.Contracts;
using Klaxon.Api.Ingestion;
using Klaxon.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Klaxon.Tests.Unit.Ingestion;

public sealed class PrometheusAlertSourceAdapterTests
{
    private static readonly PrometheusAlertSourceAdapter Adapter = new();

    // A real groupKey: the route path first, the group labels last.
    private const string GroupKey = """{}/{severity="critical"}:{alertname="HighCpu", instance="web-1"}""";

    private static JsonElement Envelope(string groupKey = GroupKey, string status = "firing") =>
        JsonSerializer.SerializeToElement(new
        {
            version = "4",
            groupKey,
            status,
            receiver = "klaxon",
            alerts = new[] { new { status, labels = new { alertname = "HighCpu" } } },
        });

    [Fact]
    public void Parse_FiringEnvelope_MapsGroupKeyAndStatus()
    {
        var request = Adapter.Parse(Envelope());

        request.Status.Should().Be(AlertIngestStatus.Firing);
        request.DedupKey.Should().NotBeEmpty();
    }

    // Alertmanager sends its status lowercase. Nothing here inherits JsonStringEnumConverter's
    // case-insensitive matching, so a comparison against "Firing" would page for nothing.
    [Fact]
    public void Parse_ResolvedEnvelope_MapsToResolved()
    {
        var request = Adapter.Parse(Envelope(status: "resolved"));

        request.Status.Should().Be(AlertIngestStatus.Resolved);
    }

    // The key has to survive the round trip, or a resolve never finds the alert its firing opened:
    // the alert stays Open holding its slot in IX_Alerts_OpenDedup, and every later firing of the
    // group deduplicates onto a finished incident and pages nobody.
    [Fact]
    public void Parse_SameGroupKey_ProducesSameDedupKeyAcrossFiringAndResolved()
    {
        var firing = Adapter.Parse(Envelope(status: "firing"));
        var resolved = Adapter.Parse(Envelope(status: "resolved"));

        resolved.DedupKey.Should().Be(firing.DedupKey);
    }

    // A group_by over a handful of labels blows the dedup column on a stock deployment. Without the
    // hash this is a 400, and Alertmanager drops 4xx permanently — nobody is ever paged for the
    // group, and Klaxon holds no record that it fired.
    [Fact]
    public void Parse_GroupKeyLongerThanTheDedupColumn_ProducesKeyTheDomainAccepts()
    {
        var request = Adapter.Parse(Envelope(groupKey: new string('g', 5000)));

        request.DedupKey.Length.Should().BeLessThanOrEqualTo(Alert.MaxDedupKeyLength);
    }

    // The discriminating labels sit at the end of a groupKey, so truncating to the column width
    // would collapse these two onto one incident and page for only the first.
    [Fact]
    public void Parse_GroupKeysSharingALongPrefix_ProduceDifferentDedupKeys()
    {
        var prefix = new string('g', 300);

        var first = Adapter.Parse(Envelope(groupKey: prefix + "instance=\"web-1\""));
        var second = Adapter.Parse(Envelope(groupKey: prefix + "instance=\"web-2\""));

        second.DedupKey.Should().NotBe(first.DedupKey);
    }

    // Alertmanager batches a group into one notification. Reading a single alert out of the array
    // would silently drop the rest of the group from what a responder reads.
    [Fact]
    public void Parse_GroupOfManyAlerts_KeepsEveryAlertInThePayload()
    {
        var body = JsonSerializer.SerializeToElement(new
        {
            groupKey = GroupKey,
            status = "firing",
            truncatedAlerts = 47,
            alerts = new[]
            {
                new { fingerprint = "aaa" },
                new { fingerprint = "bbb" },
                new { fingerprint = "ccc" },
            },
        });

        var request = Adapter.Parse(body);

        using var payload = JsonDocument.Parse(request.Payload.GetRawText());
        payload.RootElement.GetProperty("alerts").GetArrayLength().Should().Be(3);
        payload.RootElement.GetProperty("truncatedAlerts").GetInt32().Should().Be(47);
        payload.RootElement.GetProperty("groupKey").GetString().Should().Be(GroupKey);
    }

    [Fact]
    public void Parse_BlankGroupKey_Throws()
    {
        var act = () => Adapter.Parse(Envelope(groupKey: "  "));
        act.Should().Throw<ArgumentException>().WithParameterName("groupKey");
    }

    [Fact]
    public void Parse_MissingGroupKey_Throws()
    {
        var body = JsonSerializer.SerializeToElement(new { status = "firing" });

        var act = () => Adapter.Parse(body);
        act.Should().Throw<JsonException>();
    }

    // GetString() on a number throws InvalidOperationException, which no exception-handler arm maps:
    // a 500, and a sender that retries 5xx would repeat it forever.
    [Fact]
    public void Parse_GroupKeyIsNotAString_Throws()
    {
        var body = JsonSerializer.SerializeToElement(new { groupKey = 42, status = "firing" });

        var act = () => Adapter.Parse(body);
        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("Firing")]
    [InlineData("")]
    public void Parse_UnknownStatus_Throws(string status)
    {
        var act = () => Adapter.Parse(Envelope(status: status));
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Parse_MissingStatus_Throws()
    {
        var body = JsonSerializer.SerializeToElement(new { groupKey = GroupKey });

        var act = () => Adapter.Parse(body);
        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    public void Parse_NonObjectBody_Throws(string body)
    {
        using var document = JsonDocument.Parse(body);
        var element = document.RootElement.Clone();

        var act = () => Adapter.Parse(element);
        act.Should().Throw<JsonException>();
    }
}
