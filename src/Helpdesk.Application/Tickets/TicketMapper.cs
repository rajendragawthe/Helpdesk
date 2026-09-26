using System.Text.RegularExpressions;
using Helpdesk.Application.Classification;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;

namespace Helpdesk.Application.Tickets;

internal static partial class TicketMapper
{
    public static TicketListItem ToListItem(Ticket ticket) => new(
        ticket.Id,
        ticket.Subject,
        ticket.RequesterEmail,
        ticket.Status,
        ticket.Classification?.Category,
        ticket.Classification?.Summary,
        ticket.Classification?.Confidence,
        ToAssignee(ticket),
        ticket.CreatedAt,
        ticket.UpdatedAt,
        !string.IsNullOrWhiteSpace(ticket.DraftReply),
        (int)ticket.ReviewReasons,
        ticket.ReviewReasons != ReviewReasons.None);

    public static TicketDetail ToDetail(Ticket ticket) => new(
        ticket.Id,
        ticket.Subject,
        ticket.RequesterEmail,
        ticket.Status,
        ticket.Classification?.Category,
        ticket.Classification?.Summary,
        ticket.Classification?.Confidence,
        ToAssignee(ticket),
        ticket.CreatedAt,
        ticket.UpdatedAt,
        !string.IsNullOrWhiteSpace(ticket.DraftReply),
        (int)ticket.ReviewReasons,
        ticket.ReviewReasons != ReviewReasons.None,
        ticket.DraftReply,
        ticket.Messages.OrderBy(m => m.ReceivedAt).Select(ToMessage).ToList());

    // Customer email bodies are arbitrary HTML: BodyText is always the stripped plain text. BodyHtml carries the
    // raw HTML for customer messages only (the client renders it inside a script-less sandboxed iframe, the only
    // trust boundary) and is null when there is nothing worth rendering as HTML. Agent replies are plain text we
    // stored ourselves: returned as written, never as HTML.
    private static TicketMessageDto ToMessage(Message message) => new(
        message.Id,
        message.Sender,
        message.IsFromUser,
        message.ReceivedAt,
        message.IsFromUser ? HtmlText.ToPlainText(message.Body) : message.Body,
        ToBodyHtml(message));

    private static string? ToBodyHtml(Message message)
    {
        if (!message.IsFromUser
            || string.IsNullOrWhiteSpace(message.Body)
            || message.Body.Length > TicketWorkflowService.MaxBodyHtmlLength)
        {
            return null;
        }

        // Plain-text emails have no tags: in an HTML iframe their line breaks would collapse, and the plain-text
        // view already renders them correctly.
        return LooksLikeHtml(message.Body) ? message.Body : null;
    }

    private static bool LooksLikeHtml(string body)
    {
        try
        {
            return HtmlTagRegex().IsMatch(body);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [GeneratedRegex(
        @"<(html|head|body|div|p|br|table|span|a|b|i|u|strong|em|ul|ol|li|h[1-6]|img|style|font|center|blockquote)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex HtmlTagRegex();

    private static TicketAssignee? ToAssignee(Ticket ticket) =>
        ticket.AssignedUser is null ? null : new TicketAssignee(ticket.AssignedUser.Id, ticket.AssignedUser.DisplayName);
}
