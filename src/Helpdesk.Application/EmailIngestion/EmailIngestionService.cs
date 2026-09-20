using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.EmailIngestion;

public class EmailIngestionService(
    IMailClient mailClient,
    IServiceScopeFactory scopeFactory,
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
                // Each message gets its own DI scope (and therefore its own HelpdeskDbContext),
                // so a persistence failure for one message (e.g. a constraint violation) can't
                // leave a poisoned entity in a shared change tracker that then blocks every
                // subsequent message in the same tick.
                using var scope = scopeFactory.CreateScope();
                var ticketRepository = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
                var messageRepository = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

                await ProcessEmailAsync(email, ticketRepository, messageRepository);
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

    private async Task ProcessEmailAsync(
        InboundEmailMessage email,
        ITicketRepository ticketRepository,
        IMessageRepository messageRepository)
    {
        var existingMessage = await messageRepository.GetByExternalMessageIdAsync(email.ExternalMessageId);
        if (existingMessage is not null)
        {
            return;
        }

        var ticket = await ticketRepository.GetByConversationIdAsync(email.ConversationId);

        if (ticket is null)
        {
            ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                Subject = email.Subject,
                RequesterEmail = email.FromAddress,
                Status = TicketStatus.New,
                ConversationId = email.ConversationId,
                CreatedAt = email.ReceivedAt,
                UpdatedAt = email.ReceivedAt,
            };
            await ticketRepository.AddAsync(ticket);
        }
        else
        {
            ticket.UpdatedAt = email.ReceivedAt;
            await ticketRepository.UpdateAsync(ticket);
        }

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
        await messageRepository.AddAsync(message);

        await mailClient.MarkAsProcessedAsync(email.ExternalMessageId);
    }
}
