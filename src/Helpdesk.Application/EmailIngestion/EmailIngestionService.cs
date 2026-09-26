using Helpdesk.Application.Classification;
using Helpdesk.Application.DraftReply;
using Helpdesk.Application.Review;
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

                var newTicketId = await ProcessEmailAsync(email, ticketRepository, messageRepository);

                // Only a brand-new ticket is classified, drafted and review-flagged (never a reply appended to an
                // existing conversation). These services are optional: they are only registered when
                // OpenRouter is configured, so ingestion keeps working without AI. Drafting runs after
                // classification so the drafter can use the stored category. The drafter gets its own
                // fresh scope (and DbContext) so a failed classification save, whose Added entity would
                // stay tracked and be retried, cannot poison the drafter's change tracker: the two fail
                // independently.
                if (newTicketId is { } ticketId)
                {
                    if (scope.ServiceProvider.GetService<IClassificationService>() is { } classifier)
                    {
                        await classifier.ClassifyTicketAsync(ticketId, cancellationToken);
                    }

                    using var draftScope = scopeFactory.CreateScope();
                    if (draftScope.ServiceProvider.GetService<IDraftReplyService>() is { } drafter)
                    {
                        await drafter.DraftReplyAsync(ticketId, cancellationToken);
                    }

                    // Review flags are derived from what classification and drafting actually stored, so they
                    // run last, in their own scope for the same isolation reason as the drafter.
                    using var reviewScope = scopeFactory.CreateScope();
                    if (reviewScope.ServiceProvider.GetService<IReviewFlagService>() is { } reviewer)
                    {
                        await reviewer.EvaluateAsync(ticketId, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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

    private async Task<Guid?> ProcessEmailAsync(
        InboundEmailMessage email,
        ITicketRepository ticketRepository,
        IMessageRepository messageRepository)
    {
        var existingMessage = await messageRepository.GetByExternalMessageIdAsync(email.ExternalMessageId);
        if (existingMessage is not null)
        {
            return null;
        }

        Guid? newTicketId = null;
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
            newTicketId = ticket.Id;
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
        return newTicketId;
    }
}
