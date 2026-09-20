using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeMessageRepository : IMessageRepository
{
    public List<Message> Messages { get; } = [];

    public string? ThrowOnAddForExternalMessageId { get; set; }

    public Task<IReadOnlyList<Message>> GetByTicketIdAsync(Guid ticketId)
    {
        return Task.FromResult<IReadOnlyList<Message>>(Messages.Where(m => m.TicketId == ticketId).ToList());
    }

    public Task<Message?> GetByExternalMessageIdAsync(string externalMessageId)
    {
        return Task.FromResult(Messages.FirstOrDefault(m => m.ExternalMessageId == externalMessageId));
    }

    public Task AddAsync(Message message)
    {
        if (message.ExternalMessageId == ThrowOnAddForExternalMessageId)
        {
            throw new InvalidOperationException("Simulated persistence failure.");
        }

        Messages.Add(message);
        return Task.CompletedTask;
    }
}
