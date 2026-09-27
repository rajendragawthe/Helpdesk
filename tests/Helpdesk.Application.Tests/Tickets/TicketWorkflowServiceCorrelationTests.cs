using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Application.Tickets;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;

namespace Helpdesk.Application.Tests.Tickets;

public class TicketWorkflowServiceCorrelationTests
{
    [Fact]
    public async Task ClaimAsync_PushesTicketIdScope()
    {
        var tickets = new FakeTicketRepository();
        var alice = new User { Id = Guid.NewGuid(), Email = "alice@example.com", DisplayName = "Alice", Role = Role.Agent };
        tickets.Users.Add(alice);
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Charged twice",
            RequesterEmail = "customer@example.com",
            Status = TicketStatus.InReview,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        tickets.Tickets.Add(ticket);
        var logger = new RecordingLogger<TicketWorkflowService>();
        var service = new TicketWorkflowService(tickets, logger, mailClient: null);

        await service.ClaimAsync(new TicketCaller(alice.Id, alice.Email, IsAdmin: false), ticket.Id);

        Assert.Contains(logger.Scopes, s => Equals(s["TicketId"], ticket.Id));
    }
}
