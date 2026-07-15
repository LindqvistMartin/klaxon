namespace Klaxon.Core.Entities;

// Single source of truth for OutboxMessage.Type values. The engine writes these constants and the
// dispatcher's tests pin them; nothing else should spell the strings out again.
public static class OutboxMessageTypes
{
    // Payload: EscalationId, Level.
    public const string EscalationLevelPaged = "EscalationLevelPaged";

    // Payload: EscalationId.
    public const string EscalationExhausted = "EscalationExhausted";
}
