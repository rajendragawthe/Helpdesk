using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Review;

public class ReviewFlagService(
    ITicketRepository ticketRepository,
    ReviewOptions options,
    ILogger<ReviewFlagService> logger) : IReviewFlagService
{
    public async Task EvaluateAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId);
            if (ticket is null)
            {
                logger.LogWarning("Cannot evaluate review flags for ticket {TicketId}: not found.", ticketId);
                return;
            }

            var reasons = ReviewPolicy.Evaluate(ticket.Classification, ticket.DraftReply, options.ConfidenceThreshold);
            if (reasons == ticket.ReviewReasons)
            {
                return;
            }

            ticket.ReviewReasons = reasons;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
            await ticketRepository.UpdateAsync(ticket);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to evaluate review flags for ticket {TicketId}; leaving it unflagged.", ticketId);
        }
    }
}
