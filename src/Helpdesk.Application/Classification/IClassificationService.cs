namespace Helpdesk.Application.Classification;

public interface IClassificationService
{
    /// <summary>
    /// Classifies and summarizes the ticket if it has not been classified yet. Never throws for
    /// AI or persistence failures (logs and leaves the ticket unclassified); only cancellation propagates.
    /// </summary>
    Task ClassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unconditionally (re)classifies the ticket from its latest customer message — used when a
    /// customer reply reopens an InReview/Replied ticket. Unlike <see cref="ClassifyTicketAsync"/>,
    /// this has no "already classified" guard: it updates the existing Classification in place, or
    /// creates one if the ticket never had one. Never throws for AI or persistence failures (logs and
    /// leaves the previous classification, if any, unchanged); only cancellation propagates.
    /// </summary>
    Task ReclassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default);
}
