using Helpdesk.Core.Enums;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeTicketRepository : ITicketRepository
{
    public List<Ticket> Tickets { get; } = [];

    public Task<Ticket?> GetByIdAsync(Guid id)
    {
        return Task.FromResult(Tickets.FirstOrDefault(t => t.Id == id));
    }

    public Task<Ticket?> GetByConversationIdAsync(string conversationId)
    {
        return Task.FromResult(Tickets.FirstOrDefault(t => t.ConversationId == conversationId));
    }

    public Task<IReadOnlyList<Ticket>> GetAllAsync()
    {
        return Task.FromResult<IReadOnlyList<Ticket>>(Tickets);
    }

    public Task AddAsync(Ticket ticket)
    {
        Tickets.Add(ticket);
        return Task.CompletedTask;
    }

    public Exception? UpdateException { get; set; }

    public int UpdateCount { get; private set; }

    public Task UpdateAsync(Ticket ticket)
    {
        UpdateCount++;
        if (UpdateException is not null)
        {
            throw UpdateException;
        }

        return Task.CompletedTask;
    }

    /// <summary>Users the fake can resolve as assignees (mirrors the AssignedUser include of the real repository).</summary>
    public List<User> Users { get; } = [];

    public Exception? RecordReplyException { get; set; }

    public Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, Guid userId)
    {
        IEnumerable<Ticket> query = Tickets;
        query = filter switch
        {
            TicketFilter.Queue => query.Where(t =>
                t.Status != TicketStatus.Replied && (t.AssignedUserId == null || t.AssignedUserId == userId)),
            TicketFilter.Mine => query.Where(t => t.AssignedUserId == userId),
            _ => query,
        };
        return Task.FromResult<IReadOnlyList<Ticket>>(query.OrderByDescending(t => t.CreatedAt).ToList());
    }

    public Task<Ticket?> GetDetailAsync(Guid id)
    {
        return Task.FromResult(Tickets.FirstOrDefault(t => t.Id == id));
    }

    public Task<bool> TryClaimAsync(Guid ticketId, Guid userId)
    {
        var ticket = Tickets.FirstOrDefault(t => t.Id == ticketId);
        if (ticket is null || (ticket.AssignedUserId is not null && ticket.AssignedUserId != userId))
        {
            return Task.FromResult(false);
        }

        ticket.AssignedUserId = userId;
        ticket.AssignedUser = Users.FirstOrDefault(u => u.Id == userId);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(true);
    }

    public Task ReleaseAsync(Guid ticketId)
    {
        var ticket = Tickets.FirstOrDefault(t => t.Id == ticketId);
        if (ticket is not null)
        {
            ticket.AssignedUserId = null;
            ticket.AssignedUser = null;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task RecordReplyAsync(Guid ticketId, Message reply)
    {
        if (RecordReplyException is not null)
        {
            throw RecordReplyException;
        }

        var ticket = Tickets.First(t => t.Id == ticketId);
        reply.TicketId = ticketId;
        ticket.Messages.Add(reply);
        ticket.Status = TicketStatus.Replied;
        ticket.UpdatedAt = reply.ReceivedAt;
        return Task.CompletedTask;
    }
}
