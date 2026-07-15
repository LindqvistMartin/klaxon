using Klaxon.Core.Entities;
using FluentAssertions;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class OutboxMessageTests
{
    [Fact]
    public void Constructor_StartsUnprocessed()
    {
        var message = new OutboxMessage(OutboxMessageTypes.EscalationLevelPaged, """{"Level":0}""");

        message.Type.Should().Be(OutboxMessageTypes.EscalationLevelPaged);
        message.Payload.Should().Be("""{"Level":0}""");
        message.ProcessedAt.Should().BeNull();
        message.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Constructor_BlankType_Throws()
    {
        var act = () => new OutboxMessage(" ", "{}");
        act.Should().Throw<ArgumentException>().WithParameterName("type");
    }

    [Fact]
    public void Constructor_BlankPayload_Throws()
    {
        var act = () => new OutboxMessage(OutboxMessageTypes.EscalationExhausted, "");
        act.Should().Throw<ArgumentException>().WithParameterName("payload");
    }

    [Fact]
    public void Constructor_MalformedJsonPayload_Throws()
    {
        var act = () => new OutboxMessage(OutboxMessageTypes.EscalationExhausted, "not json");
        act.Should().Throw<ArgumentException>().WithParameterName("payload");
    }

    [Fact]
    public void MarkProcessed_StampsTheGivenInstant()
    {
        var message = new OutboxMessage(OutboxMessageTypes.EscalationExhausted, "{}");
        var now = Instant.FromUtc(2026, 7, 15, 9, 0);

        message.MarkProcessed(now);

        message.ProcessedAt.Should().Be(now);
    }
}
