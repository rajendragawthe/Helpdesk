using Helpdesk.Application.Classification;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeClassificationService : IClassificationService
{
    public List<Guid> ClassifiedTicketIds { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    public Task ClassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        ClassifiedTicketIds.Add(ticketId);
        return Task.CompletedTask;
    }
}
