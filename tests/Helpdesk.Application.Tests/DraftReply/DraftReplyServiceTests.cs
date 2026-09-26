using Helpdesk.Application.DraftReply;
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Application.Tests.DraftReply;

public class DraftReplyServiceTests
{
    private readonly FakeTicketRepository _tickets = new();
    private readonly FakeKnowledgeBase _kb = new();
    private readonly FakeAiService _ai = new();

    private DraftReplyService CreateService() =>
        new(_tickets, _kb, _ai, NullLogger<DraftReplyService>.Instance);

    private Ticket AddTicket(string? category, params Message[] messages)
    {
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Charged twice",
            RequesterEmail = "a@example.com",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        if (category is not null)
        {
            ticket.Classification = new Helpdesk.Core.Entities.Classification
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                Category = category,
                Summary = "s",
                Confidence = 0.9,
            };
        }

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

    private static Message OneMessage(string body = "<p>I was <b>charged twice</b></p>") =>
        UserMessage(body, DateTimeOffset.UtcNow);

    [Fact]
    public async Task DraftReplyAsync_NewTicket_StoresTrimmedDraftAndMovesToInReview()
    {
        var ticket = AddTicket("Billing", OneMessage());
        _ai.DraftResult = "  Hello,\n\nWe will look into it.\n\nThe Support Team\n";

        await CreateService().DraftReplyAsync(ticket.Id);

        Assert.Equal("Hello,\n\nWe will look into it.\n\nThe Support Team", ticket.DraftReply);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
        Assert.True(ticket.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task DraftReplyAsync_SendsSubjectPlainTextCategoryAndMatchedArticlesToAi()
    {
        var refund = new KbArticle("refund", "Refunds", "Billing", ["charged twice"], "Refund content");
        var unrelated = new KbArticle("hours", "Hours", "General Inquiry", ["opening hours"], "Hours content");
        _kb.Articles.AddRange([unrelated, refund]);
        var ticket = AddTicket("Billing", OneMessage());

        await CreateService().DraftReplyAsync(ticket.Id);

        var call = Assert.Single(_ai.DraftCalls);
        Assert.Equal("Charged twice", call.Subject);
        Assert.Equal("I was charged twice", call.Body);
        Assert.Equal("Billing", call.Category);
        Assert.Equal(["refund"], call.Articles.Select(a => a.Id));
    }

    [Fact]
    public async Task DraftReplyAsync_UsesFirstCustomerMessage()
    {
        var ticket = AddTicket(
            "Billing",
            UserMessage("<p>later reply</p>", DateTimeOffset.UtcNow),
            UserMessage("<p>original question</p>", DateTimeOffset.UtcNow.AddHours(-1)));

        await CreateService().DraftReplyAsync(ticket.Id);

        Assert.Equal("original question", Assert.Single(_ai.DraftCalls).Body);
    }

    [Fact]
    public async Task DraftReplyAsync_NoKbMatch_StillDraftsWithEmptyArticles()
    {
        _kb.Articles.Add(new KbArticle("hours", "Hours", "General Inquiry", ["opening hours"], "c"));
        var ticket = AddTicket("Billing", OneMessage("<p>something else entirely</p>"));

        await CreateService().DraftReplyAsync(ticket.Id);

        Assert.Empty(Assert.Single(_ai.DraftCalls).Articles);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
        Assert.NotNull(ticket.DraftReply);
    }

    [Fact]
    public async Task DraftReplyAsync_NoClassification_DraftsWithoutCategoryOrBoost()
    {
        _kb.Articles.Add(new KbArticle("refund", "Refunds", "Billing", ["charged twice"], "c"));
        var ticket = AddTicket(null, OneMessage());

        await CreateService().DraftReplyAsync(ticket.Id);

        var call = Assert.Single(_ai.DraftCalls);
        Assert.Null(call.Category);
        Assert.Equal(["refund"], call.Articles.Select(a => a.Id));
        Assert.Equal(TicketStatus.InReview, ticket.Status);
    }

    [Fact]
    public async Task DraftReplyAsync_AlreadyDrafted_DoesNothing()
    {
        var ticket = AddTicket("Billing", OneMessage());
        ticket.DraftReply = "existing draft";

        await CreateService().DraftReplyAsync(ticket.Id);

        Assert.Empty(_ai.DraftCalls);
        Assert.Equal("existing draft", ticket.DraftReply);
        Assert.Equal(TicketStatus.New, ticket.Status);
    }

    [Fact]
    public async Task DraftReplyAsync_TicketNotFound_DoesNothing()
    {
        await CreateService().DraftReplyAsync(Guid.NewGuid());

        Assert.Empty(_ai.DraftCalls);
    }

    [Fact]
    public async Task DraftReplyAsync_NoCustomerMessage_DoesNothing()
    {
        var ticket = AddTicket("Billing");

        await CreateService().DraftReplyAsync(ticket.Id);

        Assert.Empty(_ai.DraftCalls);
        Assert.Equal(TicketStatus.New, ticket.Status);
    }

    [Fact]
    public async Task DraftReplyAsync_AiThrows_SwallowsAndLeavesTicketNewWithoutDraft()
    {
        var ticket = AddTicket("Billing", OneMessage());
        _ai.DraftExceptionToThrow = new HttpRequestException("boom");

        await CreateService().DraftReplyAsync(ticket.Id);

        Assert.Null(ticket.DraftReply);
        Assert.Equal(TicketStatus.New, ticket.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task DraftReplyAsync_BlankDraft_LeavesTicketNewWithoutDraft(string blank)
    {
        var ticket = AddTicket("Billing", OneMessage());
        _ai.DraftResult = blank;

        await CreateService().DraftReplyAsync(ticket.Id);

        Assert.Null(ticket.DraftReply);
        Assert.Equal(TicketStatus.New, ticket.Status);
    }

    [Fact]
    public async Task DraftReplyAsync_PersistenceThrows_Swallows()
    {
        var ticket = AddTicket("Billing", OneMessage());
        _tickets.UpdateException = new InvalidOperationException("db down");

        await CreateService().DraftReplyAsync(ticket.Id);

        Assert.Single(_ai.DraftCalls);
    }

    [Fact]
    public async Task DraftReplyAsync_Cancelled_PropagatesCancellation()
    {
        var ticket = AddTicket("Billing", OneMessage());
        _ai.DraftExceptionToThrow = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateService().DraftReplyAsync(ticket.Id, cts.Token));
    }
}
