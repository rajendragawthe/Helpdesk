using Helpdesk.Core.Entities;

namespace Helpdesk.Core.Interfaces;

public interface IMessageRepository
{
    Task<IReadOnlyList<Message>> GetByTicketIdAsync(Guid ticketId);
    Task AddAsync(Message message);
}
