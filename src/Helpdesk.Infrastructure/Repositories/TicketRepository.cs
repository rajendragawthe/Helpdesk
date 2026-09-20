using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Repositories;

public class TicketRepository(HelpdeskDbContext dbContext) : ITicketRepository
{
    public async Task<Ticket?> GetByIdAsync(Guid id)
    {
        return await dbContext.Tickets
            .Include(t => t.Messages)
            .Include(t => t.Classification)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<Ticket?> GetByConversationIdAsync(string conversationId)
    {
        return await dbContext.Tickets
            .Include(t => t.Messages)
            .FirstOrDefaultAsync(t => t.ConversationId == conversationId);
    }

    public async Task<IReadOnlyList<Ticket>> GetAllAsync()
    {
        return await dbContext.Tickets
            .Include(t => t.Classification)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task AddAsync(Ticket ticket)
    {
        dbContext.Tickets.Add(ticket);
        await dbContext.SaveChangesAsync();
    }

    public async Task UpdateAsync(Ticket ticket)
    {
        dbContext.Tickets.Update(ticket);
        await dbContext.SaveChangesAsync();
    }
}
