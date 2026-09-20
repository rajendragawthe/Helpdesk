using Helpdesk.Core.Entities;

namespace Helpdesk.Core.Interfaces;

public interface ITicketRepository
{
    Task<Ticket?> GetByIdAsync(Guid id);
    Task<Ticket?> GetByConversationIdAsync(string conversationId);
    Task<IReadOnlyList<Ticket>> GetAllAsync();
    Task AddAsync(Ticket ticket);
    Task UpdateAsync(Ticket ticket);
}
