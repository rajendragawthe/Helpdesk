using Helpdesk.Application.Classification;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.DraftReply;

public class DraftReplyService(
    ITicketRepository ticketRepository,
    IKnowledgeBase knowledgeBase,
    IAiService aiService,
    ILogger<DraftReplyService> logger) : IDraftReplyService
{
    public async Task DraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId);
            if (ticket is null)
            {
                logger.LogWarning("Cannot draft a reply for ticket {TicketId}: not found.", ticketId);
                return;
            }

            if (!string.IsNullOrWhiteSpace(ticket.DraftReply))
            {
                return;
            }

            var firstMessage = ticket.Messages
                .Where(m => m.IsFromUser)
                .OrderBy(m => m.ReceivedAt)
                .FirstOrDefault();
            if (firstMessage is null)
            {
                logger.LogWarning("Cannot draft a reply for ticket {TicketId}: it has no customer message.", ticketId);
                return;
            }

            var body = HtmlText.ToPlainText(firstMessage.Body);
            var category = ticket.Classification?.Category;
            var articles = KbMatcher.Match(ticket.Subject, body, category, knowledgeBase.GetAll());

            var draft = await aiService.DraftReplyAsync(ticket.Subject, body, category, articles, cancellationToken);
            if (string.IsNullOrWhiteSpace(draft))
            {
                logger.LogWarning("The AI returned a blank draft for ticket {TicketId}; leaving it without a draft.", ticketId);
                return;
            }

            ticket.DraftReply = draft.Trim();
            ticket.Status = TicketStatus.InReview;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
            await ticketRepository.UpdateAsync(ticket);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to draft a reply for ticket {TicketId}; leaving it without a draft.", ticketId);
        }
    }
}
