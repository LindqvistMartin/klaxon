namespace Klaxon.Infrastructure.Ack;

// Binds the "Ack" configuration section. SigningKey has no default on purpose: an ack link is a
// bearer credential, so a deployment that forgot to set a key should fail to start rather than sign
// links anyone could forge (the guard lives in Program.cs). LinkLifetime is generous because the
// link outlives the ack window — a responder may act on a page well after the escalation stopped
// climbing.
public sealed class AckOptions
{
    public string SigningKey { get; set; } = "";
    public string LinkBaseUrl { get; set; } = "";
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromDays(7);
}
