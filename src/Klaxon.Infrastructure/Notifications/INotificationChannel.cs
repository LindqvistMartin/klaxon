using Klaxon.Core.Entities;

namespace Klaxon.Infrastructure.Notifications;

// A delivery target for an outbox message. ADR-003 buys this seam up front: a new way to page is
// one implementation plus a DI registration, with no change to the dispatcher.
public interface INotificationChannel
{
    Task SendAsync(OutboxMessage message, CancellationToken cancellationToken);
}
