namespace Helpdesk.Core.Models;

public record InboundEmailMessage(
    string ExternalMessageId,
    string ConversationId,
    string FromAddress,
    string Subject,
    string BodyHtml,
    DateTimeOffset ReceivedAt);
