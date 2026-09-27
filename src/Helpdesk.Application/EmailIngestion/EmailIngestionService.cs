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
    /// <summary>Result of processing one inbound email: which ticket it landed on, and which pipeline (if any) it needs.</summary>
    private readonly record struct EmailProcessingResult(Guid TicketId, bool IsNewTicket, bool NeedsReopen);

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

                var result = await ProcessEmailAsync(email, ticketRepository, messageRepository);
                if (result is not { } processed)
                {
                    continue;
                }

                if (processed.IsNewTicket)
                {
                    // Only a brand-new ticket is classified, drafted and review-flagged this way (never a
                    // reply appended to an existing conversation). These services are optional: they are
                    // only registered when OpenRouter is configured, so ingestion keeps working without
                    // AI. Drafting runs after classification so the drafter can use the stored category.
                    // The drafter gets its own fresh scope (and DbContext) so a failed classification
                    // save, whose Added entity would stay tracked and be retried, cannot poison the
                    // drafter's change tracker: the two fail independently.
                    if (scope.ServiceProvider.GetService<IClassificationService>() is { } classifier)
                    {
                        await classifier.ClassifyTicketAsync(processed.TicketId, cancellationToken);
                    }

                    using var draftScope = scopeFactory.CreateScope();
                    if (draftScope.ServiceProvider.GetService<IDraftReplyService>() is { } drafter)
                    {
                        await drafter.DraftReplyAsync(processed.TicketId, cancellationToken);
                    }

                    // Review flags are derived from what classification and drafting actually stored, so they
                    // run last, in their own scope for the same isolation reason as the drafter.
                    using var reviewScope = scopeFactory.CreateScope();
                    if (reviewScope.ServiceProvider.GetService<IReviewFlagService>() is { } reviewer)
                    {
                        await reviewer.EvaluateAsync(processed.TicketId, cancellationToken);
                    }
                }
                else if (processed.NeedsReopen)
                {
                    // A customer reply reopened an InReview/Replied ticket (its status was already
                    // flipped Replied -> InReview inside ProcessEmailAsync, unconditionally, so the
                    // ticket reappears in the queue even if the AI calls below fail). Reclassify from
                    // the latest message, then redraft from the full thread, then re-evaluate review
                    // flags - same per-step scope isolation and AI-optional gating as the new-ticket path.
                    if (scope.ServiceProvider.GetService<IClassificationService>() is { } classifier)
                    {
                        await classifier.ReclassifyTicketAsync(processed.TicketId, cancellationToken);
                    }

                    using var draftScope = scopeFactory.CreateScope();
                    if (draftScope.ServiceProvider.GetService<IDraftReplyService>() is { } drafter)
                    {
                        await drafter.RedraftReplyAsync(processed.TicketId, cancellationToken);
                    }

                    using var reviewScope = scopeFactory.CreateScope();
                    if (reviewScope.ServiceProvider.GetService<IReviewFlagService>() is { } reviewer)
                    {
                        await reviewer.EvaluateAsync(processed.TicketId, cancellationToken);
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

    private async Task<EmailProcessingResult?> ProcessEmailAsync(
        InboundEmailMessage email,
        ITicketRepository ticketRepository,
        IMessageRepository messageRepository)
    {
        var existingMessage = await messageRepository.GetByExternalMessageIdAsync(email.ExternalMessageId);
        if (existingMessage is not null)
        {
            return null;
        }

        var ticket = await ticketRepository.GetByConversationIdAsync(email.ConversationId);

        bool isNewTicket;
        bool needsReopen;

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
            isNewTicket = true;
            needsReopen = false;
        }
        else
        {
            isNewTicket = false;
            // A reply is only "reopen-eligible" if the ticket had already gone through the
            // first-message pipeline (InReview or Replied). A reply arriving while the ticket is
            // still New (that pipeline mid-flight or previously failed) is left alone, matching
            // today's behavior: the message is stored, nothing else happens.
            needsReopen = ticket.Status is TicketStatus.InReview or TicketStatus.Replied;

            // Applied unconditionally, independent of whether reclassify/redraft below succeed, so
            // the ticket reliably reappears in the Queue/Mine views even on an AI failure.
            if (ticket.Status == TicketStatus.Replied)
            {
                ticket.Status = TicketStatus.InReview;
            }

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
        return new EmailProcessingResult(ticket.Id, isNewTicket, needsReopen);
    }
}
