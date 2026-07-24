using System.Text.Json;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Ack;
using Microsoft.Extensions.Logging;

namespace Klaxon.Infrastructure.Notifications;

// Writes the page to the application log — the one channel that needs no configuration and no
// network, which makes it the sensible default for a single-operator deployment. A paged level also
// carries its ack link, so the loop is usable end to end from the log alone: an operator copies the
// URL and acks without an account.
public sealed class LogChannel(ILogger<LogChannel> logger, IAckLinkFactory ackLinks) : INotificationChannel
{
    public Task SendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.Type != OutboxMessageTypes.EscalationLevelPaged)
        {
            // Exhaustion is terminal — there is nothing left to ack — so it pages without a link.
            logger.LogInformation("Paging: {Type} {Payload}", message.Type, message.Payload);
            return Task.CompletedTask;
        }

        using var payload = JsonDocument.Parse(message.Payload);
        var escalationId = payload.RootElement.GetProperty("EscalationId").GetGuid();
        logger.LogInformation("Paging: {Type} {Payload} ack={AckLink}",
            message.Type, message.Payload, ackLinks.CreateLink(escalationId));
        return Task.CompletedTask;
    }
}
