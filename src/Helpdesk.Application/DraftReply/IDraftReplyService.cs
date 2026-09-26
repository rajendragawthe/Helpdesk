namespace Helpdesk.Application.DraftReply;

public interface IDraftReplyService
{
    /// <summary>
    /// Drafts an AI reply for the ticket if it has none yet, stores it on the ticket and moves the ticket to
    /// InReview. Never throws for AI or persistence failures (logs and leaves the ticket New with no draft);
    /// only cancellation propagates.
    /// </summary>
    Task DraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default);
}
