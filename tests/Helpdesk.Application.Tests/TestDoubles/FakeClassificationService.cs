using Helpdesk.Application.Classification;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeClassificationService : IClassificationService
{
    public List<Guid> ClassifiedTicketIds { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    public Task ClassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        ClassifiedTicketIds.Add(ticketId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }
}
