using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;

namespace Helpdesk.Core.Interfaces;

public interface ITicketRepository
{
    Task<Ticket?> GetByIdAsync(Guid id);
    Task<Ticket?> GetByConversationIdAsync(string conversationId);
    Task<IReadOnlyList<Ticket>> GetAllAsync();
    Task AddAsync(Ticket ticket);
    Task UpdateAsync(Ticket ticket);

    /// <summary>Tickets for the queue views, newest first, with Classification and AssignedUser loaded (no tracking).</summary>
    Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, Guid userId);

    /// <summary>One ticket with Messages, Classification and AssignedUser loaded (no tracking), or null.</summary>
    Task<Ticket?> GetDetailAsync(Guid id);

    /// <summary>
    /// Atomically assigns the ticket to the user if it is unassigned or already theirs. Returns false when it
    /// does not exist or is assigned to someone else.
    /// </summary>
    Task<bool> TryClaimAsync(Guid ticketId, Guid userId);

    /// <summary>Clears the assignee.</summary>
    Task ReleaseAsync(Guid ticketId);

    /// <summary>Stores the outbound reply message and marks the ticket Replied, in one save.</summary>
    Task RecordReplyAsync(Guid ticketId, Message reply);
}
