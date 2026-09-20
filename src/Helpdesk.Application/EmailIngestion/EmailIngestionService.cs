using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.EmailIngestion;

public class EmailIngestionService(
    IMailClient mailClient,
    ITicketRepository ticketRepository,
    IMessageRepository messageRepository,
    ILogger<EmailIngestionService> logger)
{
    public async Task IngestNewEmailsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<InboundEmailMessage> emails;
        try
        {
            emails = await mailClient.FetchNewMessagesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch new emails from the mail client.");
            return;
        }

        foreach (var email in emails)
        {
            try
            {
                await ProcessEmailAsync(email);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to process email {ExternalMessageId} (conversation {ConversationId}).",
                    email.ExternalMessageId,
                    email.ConversationId);
            }
        }
    }

    private async Task ProcessEmailAsync(InboundEmailMessage email)
    {
        var existingMessage = await messageRepository.GetByExternalMessageIdAsync(email.ExternalMessageId);
        if (existingMessage is not null)
        {
            return;
        }

        var existingTicket = await ticketRepository.GetByConversationIdAsync(email.ConversationId);
        var isNewTicket = existingTicket is null;

        var ticket = existingTicket ?? new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = email.Subject,
            RequesterEmail = email.FromAddress,
            Status = TicketStatus.New,
            ConversationId = email.ConversationId,
            CreatedAt = email.ReceivedAt,
            UpdatedAt = email.ReceivedAt,
        };

        var message = new Message
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            Sender = email.FromAddress,
            Body = email.BodyHtml,
            IsFromUser = true,
            ExternalMessageId = email.ExternalMessageId,
            ReceivedAt = email.ReceivedAt,
        };

        // Persist the message before the ticket so a failed message write leaves no
        // orphaned ticket behind for this conversation (see ProcessEmailAsync callers,
        // which log and continue on failure rather than letting one bad email abort the batch).
        await messageRepository.AddAsync(message);

        if (isNewTicket)
        {
            await ticketRepository.AddAsync(ticket);
        }
        else
        {
            ticket.UpdatedAt = email.ReceivedAt;
            await ticketRepository.UpdateAsync(ticket);
        }

        await mailClient.MarkAsProcessedAsync(email.ExternalMessageId);
    }
}
