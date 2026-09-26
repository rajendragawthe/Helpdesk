using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
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
            .AsNoTracking()
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

    public async Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, Guid userId)
    {
        var query = dbContext.Tickets
            .AsNoTracking()
            .Include(t => t.Classification)
            .Include(t => t.AssignedUser)
            .AsQueryable();

        query = filter switch
        {
            TicketFilter.Queue => query.Where(t =>
                t.Status != TicketStatus.Replied && (t.AssignedUserId == null || t.AssignedUserId == userId)),
            TicketFilter.Mine => query.Where(t => t.AssignedUserId == userId),
            _ => query,
        };

        return await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
    }

    public async Task<Ticket?> GetDetailAsync(Guid id)
    {
        return await dbContext.Tickets
            .AsNoTracking()
            .Include(t => t.Messages)
            .Include(t => t.Classification)
            .Include(t => t.AssignedUser)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<bool> TryClaimAsync(Guid ticketId, Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await dbContext.Tickets
            .Where(t => t.Id == ticketId && (t.AssignedUserId == null || t.AssignedUserId == userId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.AssignedUserId, (Guid?)userId)
                .SetProperty(t => t.UpdatedAt, now));
        return rows > 0;
    }

    public async Task ReleaseAsync(Guid ticketId)
    {
        var now = DateTimeOffset.UtcNow;
        await dbContext.Tickets
            .Where(t => t.Id == ticketId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.AssignedUserId, (Guid?)null)
                .SetProperty(t => t.UpdatedAt, now));
    }

    public async Task RecordReplyAsync(Guid ticketId, Message reply)
    {
        var ticket = await dbContext.Tickets.FirstAsync(t => t.Id == ticketId);
        ticket.Status = TicketStatus.Replied;
        ticket.UpdatedAt = reply.ReceivedAt;
        reply.TicketId = ticketId;
        dbContext.Messages.Add(reply);
        await dbContext.SaveChangesAsync();
    }
}
