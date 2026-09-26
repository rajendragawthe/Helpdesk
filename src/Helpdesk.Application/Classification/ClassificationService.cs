using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Classification;

public class ClassificationService(
    ITicketRepository ticketRepository,
    IClassificationRepository classificationRepository,
    IAiService aiService,
    ILogger<ClassificationService> logger) : IClassificationService
{
    public async Task ClassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId);
            if (ticket is null)
            {
                logger.LogWarning("Cannot classify ticket {TicketId}: not found.", ticketId);
                return;
            }

            if (ticket.Classification is not null)
            {
                return;
            }

            var firstMessage = ticket.Messages
                .Where(m => m.IsFromUser)
                .OrderBy(m => m.ReceivedAt)
                .FirstOrDefault();
            if (firstMessage is null)
            {
                logger.LogWarning("Cannot classify ticket {TicketId}: it has no customer message.", ticketId);
                return;
            }

            var result = await aiService.ClassifyAsync(
                ticket.Subject,
                HtmlText.ToPlainText(firstMessage.Body),
                cancellationToken);

            await classificationRepository.AddAsync(new Helpdesk.Core.Entities.Classification
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                Category = result.Category,
                Summary = result.Summary,
                Confidence = result.Confidence,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to classify ticket {TicketId}; leaving it unclassified.", ticketId);
        }
    }
}
