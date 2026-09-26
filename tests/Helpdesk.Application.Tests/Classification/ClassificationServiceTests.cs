using Helpdesk.Application.Classification;
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Application.Tests.Classification;

public class ClassificationServiceTests
{
    private readonly FakeTicketRepository _tickets = new();
    private readonly FakeClassificationRepository _classifications = new();
    private readonly FakeAiService _ai = new();

    private ClassificationService CreateService() =>
        new(_tickets, _classifications, _ai, NullLogger<ClassificationService>.Instance);

    private Ticket AddTicket(params Message[] messages)
    {
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Charged twice",
            RequesterEmail = "a@example.com",
        };
        foreach (var message in messages)
        {
            message.TicketId = ticket.Id;
            ticket.Messages.Add(message);
        }

        _tickets.Tickets.Add(ticket);
        return ticket;
    }

    private static Message UserMessage(string body, DateTimeOffset receivedAt) => new()
    {
        Id = Guid.NewGuid(),
        Sender = "a@example.com",
        Body = body,
        IsFromUser = true,
        ReceivedAt = receivedAt,
    };

    [Fact]
    public async Task ClassifyTicketAsync_UnclassifiedTicket_StoresClassificationFromAi()
    {
        var ticket = AddTicket(UserMessage("<p>I was charged <b>twice</b></p>", DateTimeOffset.UtcNow));
        _ai.Result = new ClassificationResult("Billing", "Customer was double charged.", 0.87);

        await CreateService().ClassifyTicketAsync(ticket.Id);

        var stored = Assert.Single(_classifications.Classifications);
        Assert.Equal(ticket.Id, stored.TicketId);
        Assert.Equal("Billing", stored.Category);
        Assert.Equal("Customer was double charged.", stored.Summary);
        Assert.Equal(0.87, stored.Confidence);
        Assert.NotEqual(Guid.Empty, stored.Id);
        Assert.NotEqual(default, stored.CreatedAt);
    }

    [Fact]
    public async Task ClassifyTicketAsync_SendsSubjectAndPlainTextOfFirstUserMessageToAi()
    {
        var ticket = AddTicket(
            UserMessage("<p>later reply</p>", DateTimeOffset.UtcNow),
            UserMessage("<p>I was charged <b>twice</b></p>", DateTimeOffset.UtcNow.AddHours(-1)));

        await CreateService().ClassifyTicketAsync(ticket.Id);

        var call = Assert.Single(_ai.Calls);
        Assert.Equal("Charged twice", call.Subject);
        Assert.Equal("I was charged twice", call.Body);
    }

    [Fact]
    public async Task ClassifyTicketAsync_AlreadyClassified_DoesNothing()
    {
        var ticket = AddTicket(UserMessage("<p>hi</p>", DateTimeOffset.UtcNow));
        ticket.Classification = new Helpdesk.Core.Entities.Classification { Id = Guid.NewGuid(), TicketId = ticket.Id, Category = "Other", Summary = "x" };

        await CreateService().ClassifyTicketAsync(ticket.Id);

        Assert.Empty(_ai.Calls);
        Assert.Empty(_classifications.Classifications);
    }

    [Fact]
    public async Task ClassifyTicketAsync_TicketNotFound_DoesNothing()
    {
        await CreateService().ClassifyTicketAsync(Guid.NewGuid());

        Assert.Empty(_ai.Calls);
        Assert.Empty(_classifications.Classifications);
    }

    [Fact]
    public async Task ClassifyTicketAsync_TicketHasNoUserMessage_DoesNothing()
    {
        var ticket = AddTicket();

        await CreateService().ClassifyTicketAsync(ticket.Id);

        Assert.Empty(_ai.Calls);
        Assert.Empty(_classifications.Classifications);
    }

    [Fact]
    public async Task ClassifyTicketAsync_AiThrows_SwallowsAndStoresNothing()
    {
        var ticket = AddTicket(UserMessage("<p>hi</p>", DateTimeOffset.UtcNow));
        _ai.ExceptionToThrow = new HttpRequestException("boom");

        await CreateService().ClassifyTicketAsync(ticket.Id);

        Assert.Empty(_classifications.Classifications);
    }

    [Fact]
    public async Task ClassifyTicketAsync_PersistenceThrows_Swallows()
    {
        var ticket = AddTicket(UserMessage("<p>hi</p>", DateTimeOffset.UtcNow));
        _classifications.ExceptionToThrow = new InvalidOperationException("db down");

        await CreateService().ClassifyTicketAsync(ticket.Id);

        Assert.Empty(_classifications.Classifications);
    }

    [Fact]
    public async Task ClassifyTicketAsync_Cancelled_PropagatesCancellation()
    {
        var ticket = AddTicket(UserMessage("<p>hi</p>", DateTimeOffset.UtcNow));
        _ai.ExceptionToThrow = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateService().ClassifyTicketAsync(ticket.Id, cts.Token));
    }
}
