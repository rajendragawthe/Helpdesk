namespace Helpdesk.Application.DraftReply;

public interface IDraftReplyService
{
    /// <summary>
    /// Drafts an AI reply for the ticket if it has none yet, stores it on the ticket and moves the ticket to
    /// InReview. Never throws for AI or persistence failures (logs and leaves the ticket New with no draft);
    /// only cancellation propagates.
    /// </summary>
    Task DraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unconditionally redrafts the AI reply from the ticket's full message thread (both directions)
    /// and moves it to InReview — used when a customer reply reopens an InReview/Replied ticket.
    /// Unlike <see cref="DraftReplyAsync"/>, this has no "already drafted" guard. On an AI failure or
    /// a blank result, clears DraftReply to null (never leaves a stale draft addressed to an earlier
    /// message) but still moves the ticket to InReview. Never throws for AI or persistence failures;
    /// only cancellation propagates.
    /// </summary>
    Task RedraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default);
}
