using Helpdesk.Application.Review;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeReviewFlagService(FakeDraftReplyService? drafter = null) : IReviewFlagService
{
    public List<Guid> ReviewedTicketIds { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>How many tickets the paired drafter had already drafted when reviewing was called.</summary>
    public int DraftedCountAtReviewTime { get; private set; }

    public Task EvaluateAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        DraftedCountAtReviewTime = drafter?.DraftedTicketIds.Count ?? 0;
        ReviewedTicketIds.Add(ticketId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }
}
