using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Repositories;

public class MessageRepository(HelpdeskDbContext dbContext) : IMessageRepository
{
    public async Task<IReadOnlyList<Message>> GetByTicketIdAsync(Guid ticketId)
    {
        return await dbContext.Messages
            .Where(m => m.TicketId == ticketId)
            .OrderBy(m => m.ReceivedAt)
            .ToListAsync();
    }

    public async Task AddAsync(Message message)
    {
        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync();
    }
}
