using Helpdesk.Core.Enums;

namespace Helpdesk.Application.Tickets;

public interface ITicketWorkflowService
{
    Task<TicketResult<IReadOnlyList<TicketListItem>>> ListAsync(TicketCaller caller, TicketFilter filter);

    Task<TicketResult<TicketDetail>> GetAsync(Guid ticketId);

    Task<TicketResult<TicketDetail>> ClaimAsync(TicketCaller caller, Guid ticketId);

    Task<TicketResult<TicketDetail>> ReleaseAsync(TicketCaller caller, Guid ticketId);

    /// <summary>
    /// Sends the agent's reply through the mail client, then stores it and marks the ticket Replied. Only the
    /// assignee (or an Admin) may reply, and only on a claimed ticket. Only cancellation propagates; every
    /// other failure is a result outcome.
    /// </summary>
    Task<TicketResult<TicketDetail>> SendReplyAsync(
        TicketCaller caller, Guid ticketId, string text, CancellationToken cancellationToken = default);
}
