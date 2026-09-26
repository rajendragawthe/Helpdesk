using Helpdesk.Application.DraftReply;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeDraftReplyService(FakeClassificationService? classifier = null) : IDraftReplyService
{
    public List<Guid> DraftedTicketIds { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>How many tickets the paired classifier had already classified when drafting was called.</summary>
    public int ClassifiedCountAtDraftTime { get; private set; }

    public Task DraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        ClassifiedCountAtDraftTime = classifier?.ClassifiedTicketIds.Count ?? 0;
        DraftedTicketIds.Add(ticketId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }
}
