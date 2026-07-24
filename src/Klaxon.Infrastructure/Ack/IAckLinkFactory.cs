namespace Klaxon.Infrastructure.Ack;

// Builds the absolute ack URL a page carries. Separate from the token service because a channel wants
// a link, not a bare token, and the base URL is a deployment concern the codec has no business knowing.
public interface IAckLinkFactory
{
    string CreateLink(Guid escalationId);
}
