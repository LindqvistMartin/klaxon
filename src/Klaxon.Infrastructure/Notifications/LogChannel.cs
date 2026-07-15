using Klaxon.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Klaxon.Infrastructure.Notifications;

// Writes the page to the application log — the one channel that needs no configuration and no
// network, which makes it the sensible default for a single-operator deployment.
public sealed class LogChannel(ILogger<LogChannel> logger) : INotificationChannel
{
    public Task SendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Paging: {Type} {Payload}", message.Type, message.Payload);
        return Task.CompletedTask;
    }
}
