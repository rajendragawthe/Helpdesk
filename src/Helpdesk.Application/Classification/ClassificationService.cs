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

    public async Task ReclassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId);
            if (ticket is null)
            {
                logger.LogWarning("Cannot reclassify ticket {TicketId}: not found.", ticketId);
                return;
            }

            var latestMessage = ticket.Messages
                .Where(m => m.IsFromUser)
                .OrderByDescending(m => m.ReceivedAt)
                .FirstOrDefault();
            if (latestMessage is null)
            {
                logger.LogWarning("Cannot reclassify ticket {TicketId}: it has no customer message.", ticketId);
                return;
            }

            Helpdesk.Core.Models.ClassificationResult result;
            try
            {
                result = await aiService.ClassifyAsync(
                    ticket.Subject,
                    HtmlText.ToPlainText(latestMessage.Body),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reclassify ticket {TicketId}; leaving its previous classification unchanged.", ticketId);
                return;
            }

            if (ticket.Classification is { } existing)
            {
                await classificationRepository.UpdateAsync(existing.Id, result.Category, result.Summary, result.Confidence);
            }
            else
            {
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reclassify ticket {TicketId}.", ticketId);
        }
    }
}
