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

    public Task UpdateAsync(Ticket ticket)
    {
        if (UpdateException is not null)
        {
            throw UpdateException;
        }

        return Task.CompletedTask;
    }
}
