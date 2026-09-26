namespace Helpdesk.Application.Classification;

public interface IClassificationService
{
    /// <summary>
    /// Classifies and summarizes the ticket if it has not been classified yet. Never throws for
    /// AI or persistence failures (logs and leaves the ticket unclassified); only cancellation propagates.
    /// </summary>
    Task ClassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default);
}
