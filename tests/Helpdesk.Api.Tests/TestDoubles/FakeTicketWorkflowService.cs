using Helpdesk.Application.Tickets;
using Helpdesk.Core.Enums;

namespace Helpdesk.Api.Tests.TestDoubles;

public class FakeTicketWorkflowService : ITicketWorkflowService
{
    public TicketResult<IReadOnlyList<TicketListItem>> ListResult { get; set; } =
        TicketResult<IReadOnlyList<TicketListItem>>.Ok([]);

    public TicketResult<TicketDetail> DetailResult { get; set; } =
        TicketResult<TicketDetail>.Fail(TicketOutcome.NotFound, "Ticket not found.");

    public TicketCaller? LastCaller { get; private set; }
    public TicketFilter? LastFilter { get; private set; }
    public Guid? LastTicketId { get; private set; }
    public string? LastText { get; private set; }
    public string? LastAction { get; private set; }

    public Task<TicketResult<IReadOnlyList<TicketListItem>>> ListAsync(TicketCaller caller, TicketFilter filter)
    {
        LastAction = "list";
        LastCaller = caller;
        LastFilter = filter;
        return Task.FromResult(ListResult);
    }

    public Task<TicketResult<TicketDetail>> GetAsync(Guid ticketId)
    {
        LastAction = "get";
        LastTicketId = ticketId;
        return Task.FromResult(DetailResult);
    }

    public Task<TicketResult<TicketDetail>> ClaimAsync(TicketCaller caller, Guid ticketId)
    {
        LastAction = "claim";
        LastCaller = caller;
        LastTicketId = ticketId;
        return Task.FromResult(DetailResult);
    }

    public Task<TicketResult<TicketDetail>> ReleaseAsync(TicketCaller caller, Guid ticketId)
    {
        LastAction = "release";
        LastCaller = caller;
        LastTicketId = ticketId;
        return Task.FromResult(DetailResult);
    }

    public Task<TicketResult<TicketDetail>> SendReplyAsync(
        TicketCaller caller, Guid ticketId, string text, CancellationToken cancellationToken = default)
    {
        LastAction = "reply";
        LastCaller = caller;
        LastTicketId = ticketId;
        LastText = text;
        return Task.FromResult(DetailResult);
    }
}
