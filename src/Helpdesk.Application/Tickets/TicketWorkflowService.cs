using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Tickets;

public class TicketWorkflowService(
    ITicketRepository ticketRepository,
    ILogger<TicketWorkflowService> logger,
    IMailClient? mailClient = null) : ITicketWorkflowService
{
    public const int MaxReplyLength = 10_000;

    public async Task<TicketResult<IReadOnlyList<TicketListItem>>> ListAsync(TicketCaller caller, TicketFilter filter)
    {
        if (filter == TicketFilter.All && !caller.IsAdmin)
        {
            return TicketResult<IReadOnlyList<TicketListItem>>.Fail(
                TicketOutcome.Forbidden, "Only admins can list all tickets.");
        }

        var tickets = await ticketRepository.ListAsync(filter, caller.UserId);
        return TicketResult<IReadOnlyList<TicketListItem>>.Ok(tickets.Select(TicketMapper.ToListItem).ToList());
    }

    public async Task<TicketResult<TicketDetail>> GetAsync(Guid ticketId)
    {
        var ticket = await ticketRepository.GetDetailAsync(ticketId);
        return ticket is null ? NotFound() : TicketResult<TicketDetail>.Ok(TicketMapper.ToDetail(ticket));
    }

    public async Task<TicketResult<TicketDetail>> ClaimAsync(TicketCaller caller, Guid ticketId)
    {
        var existing = await ticketRepository.GetDetailAsync(ticketId);
        if (existing is null)
        {
            return NotFound();
        }

        if (!await ticketRepository.TryClaimAsync(ticketId, caller.UserId))
        {
            var holder = existing.AssignedUser?.DisplayName ?? "another agent";
            return TicketResult<TicketDetail>.Fail(TicketOutcome.Conflict, $"This ticket is already assigned to {holder}.");
        }

        return await GetAsync(ticketId);
    }

    public async Task<TicketResult<TicketDetail>> ReleaseAsync(TicketCaller caller, Guid ticketId)
    {
        var ticket = await ticketRepository.GetDetailAsync(ticketId);
        if (ticket is null)
        {
            return NotFound();
        }

        if (ticket.AssignedUserId is null)
        {
            return TicketResult<TicketDetail>.Ok(TicketMapper.ToDetail(ticket));
        }

        if (ticket.AssignedUserId != caller.UserId && !caller.IsAdmin)
        {
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.Forbidden, "Only the assignee or an admin can release this ticket.");
        }

        await ticketRepository.ReleaseAsync(ticketId);
        return await GetAsync(ticketId);
    }

    public async Task<TicketResult<TicketDetail>> SendReplyAsync(
        TicketCaller caller, Guid ticketId, string text, CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId);
        if (ticket is null)
        {
            return NotFound();
        }

        if (ticket.AssignedUserId is null)
        {
            return TicketResult<TicketDetail>.Fail(TicketOutcome.Conflict, "Claim this ticket before replying.");
        }

        if (ticket.AssignedUserId != caller.UserId && !caller.IsAdmin)
        {
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.Forbidden, "Only the assigned agent (or an admin) can reply to this ticket.");
        }

        var body = text?.Trim();
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxReplyLength)
        {
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.Invalid, $"The reply must not be blank and must be at most {MaxReplyLength} characters.");
        }

        var target = ticket.Messages
            .Where(m => m.IsFromUser && !string.IsNullOrEmpty(m.ExternalMessageId))
            .OrderByDescending(m => m.ReceivedAt)
            .FirstOrDefault();
        if (target is null)
        {
            return TicketResult<TicketDetail>.Fail(TicketOutcome.Invalid, "This ticket has no customer message to reply to.");
        }

        if (mailClient is null)
        {
            return TicketResult<TicketDetail>.Fail(TicketOutcome.SendFailed, "Email sending is not configured.");
        }

        try
        {
            await mailClient.SendReplyAsync(target.ExternalMessageId!, body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send the reply for ticket {TicketId}.", ticketId);
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.SendFailed, "The reply could not be sent. Nothing was changed; please try again.");
        }

        try
        {
            await ticketRepository.RecordReplyAsync(ticketId, new Message
            {
                Id = Guid.NewGuid(),
                Sender = caller.Email,
                Body = body,
                IsFromUser = false,
                ExternalMessageId = null,
                ReceivedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The reply for ticket {TicketId} WAS SENT to the customer but saving it failed.", ticketId);
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.SentButNotSaved,
                "The email was sent, but saving it failed. Do not send it again; ask an admin to check the ticket.");
        }

        return await GetAsync(ticketId);
    }

    private static TicketResult<TicketDetail> NotFound() =>
        TicketResult<TicketDetail>.Fail(TicketOutcome.NotFound, "Ticket not found.");
}
