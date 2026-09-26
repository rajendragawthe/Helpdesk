using Helpdesk.Application.Classification;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;

namespace Helpdesk.Application.Tickets;

internal static class TicketMapper
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

    // Customer email bodies are arbitrary HTML: only ever return them as plain text. Agent replies are
    // plain text we stored ourselves, so they are returned as written.
    private static TicketMessageDto ToMessage(Message message) => new(
        message.Id,
        message.Sender,
        message.IsFromUser,
        message.ReceivedAt,
        message.IsFromUser ? HtmlText.ToPlainText(message.Body) : message.Body);

    private static TicketAssignee? ToAssignee(Ticket ticket) =>
        ticket.AssignedUser is null ? null : new TicketAssignee(ticket.AssignedUser.Id, ticket.AssignedUser.DisplayName);
}
