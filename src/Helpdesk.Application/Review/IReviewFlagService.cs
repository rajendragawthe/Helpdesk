namespace Helpdesk.Application.Review;

public interface IReviewFlagService
{
    /// <summary>
    /// Recomputes the ticket's ReviewReasons from its stored classification and draft and persists them
    /// if they changed. Never modifies status, draft or classification. Never throws for lookup or
    /// persistence failures (logs and leaves the ticket as is); only cancellation propagates.
    /// </summary>
    Task EvaluateAsync(Guid ticketId, CancellationToken cancellationToken = default);
}
