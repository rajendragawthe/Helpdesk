using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Application.Tickets;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using ClassificationEntity = Helpdesk.Core.Entities.Classification;

namespace Helpdesk.Application.Tests.Tickets;

public class TicketWorkflowServiceTests
{
    private static readonly Guid AliceId = Guid.NewGuid();
    private static readonly Guid BobId = Guid.NewGuid();

    private readonly FakeTicketRepository _tickets = new();
    private readonly FakeMailClient _mail = new();
    private readonly User _alice = new() { Id = AliceId, Email = "alice@example.com", DisplayName = "Alice", Role = Role.Agent };
    private readonly User _bob = new() { Id = BobId, Email = "bob@example.com", DisplayName = "Bob", Role = Role.Agent };

    public TicketWorkflowServiceTests()
    {
        _tickets.Users.AddRange([_alice, _bob]);
    }

    private TicketWorkflowService Create(bool withMailClient = true) =>
        new(_tickets, NullLogger<TicketWorkflowService>.Instance, withMailClient ? _mail : null);

    private static TicketCaller Agent(Guid id, string email) => new(id, email, IsAdmin: false);
    private static TicketCaller AliceCaller => Agent(AliceId, "alice@example.com");
    private static TicketCaller BobCaller => Agent(BobId, "bob@example.com");
    private static TicketCaller AdminCaller => new(Guid.NewGuid(), "admin@example.com", IsAdmin: true);

    private static Message Customer(string body, DateTimeOffset at, string? externalId = "ext-1") => new()
    {
        Id = Guid.NewGuid(),
        Sender = "customer@example.com",
        Body = body,
        IsFromUser = true,
        ExternalMessageId = externalId,
        ReceivedAt = at,
    };

    private static Message AgentMessage(string body, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        Sender = "alice@example.com",
        Body = body,
        IsFromUser = false,
        ExternalMessageId = "agent-ext",
        ReceivedAt = at,
    };

    private Ticket AddTicket(
        TicketStatus status = TicketStatus.InReview,
        User? assignedTo = null,
        string? draft = "AI draft",
        DateTimeOffset? createdAt = null,
        params Message[] messages)
    {
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Charged twice",
            RequesterEmail = "customer@example.com",
            Status = status,
            DraftReply = draft,
            AssignedUserId = assignedTo?.Id,
            AssignedUser = assignedTo,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        ticket.Classification = new ClassificationEntity
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            Category = "Billing",
            Summary = "Double charge.",
            Confidence = 0.9,
        };
        foreach (var message in messages)
        {
            message.TicketId = ticket.Id;
            ticket.Messages.Add(message);
        }

        _tickets.Tickets.Add(ticket);
        return ticket;
    }

    private Ticket AddReplyableTicket(User? assignedTo, TicketStatus status = TicketStatus.InReview) =>
        AddTicket(status, assignedTo, "AI draft", null, Customer("<p>Please help</p>", DateTimeOffset.UtcNow.AddMinutes(-30)));

    // ---- List ----

    [Fact]
    public async Task ListAsync_Queue_ShowsUnassignedAndMineButNotOthersOrReplied()
    {
        var unassigned = AddTicket();
        var mine = AddTicket(assignedTo: _alice);
        AddTicket(assignedTo: _bob);
        AddTicket(status: TicketStatus.Replied);

        var result = await Create().ListAsync(AliceCaller, TicketFilter.Queue);

        Assert.True(result.IsSuccess);
        Assert.Equivalent(new[] { unassigned.Id, mine.Id }, result.Value!.Select(t => t.Id));
    }

    [Fact]
    public async Task ListAsync_Mine_ShowsOnlyMyTicketsIncludingReplied()
    {
        var open = AddTicket(assignedTo: _alice);
        var done = AddTicket(status: TicketStatus.Replied, assignedTo: _alice);
        AddTicket(assignedTo: _bob);
        AddTicket();

        var result = await Create().ListAsync(AliceCaller, TicketFilter.Mine);

        Assert.Equivalent(new[] { open.Id, done.Id }, result.Value!.Select(t => t.Id));
    }

    [Fact]
    public async Task ListAsync_All_AdminSeesEverything()
    {
        AddTicket();
        AddTicket(assignedTo: _bob);
        AddTicket(status: TicketStatus.Replied);

        var result = await Create().ListAsync(AdminCaller, TicketFilter.All);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.Count);
    }

    [Fact]
    public async Task ListAsync_All_NonAdminIsForbidden()
    {
        AddTicket();

        var result = await Create().ListAsync(AliceCaller, TicketFilter.All);

        Assert.Equal(TicketOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task ListAsync_MapsRowFields()
    {
        var ticket = AddTicket(assignedTo: _alice, draft: "AI draft");

        var row = Assert.Single((await Create().ListAsync(AliceCaller, TicketFilter.Mine)).Value!);

        Assert.Equal(ticket.Id, row.Id);
        Assert.Equal("Charged twice", row.Subject);
        Assert.Equal("customer@example.com", row.RequesterEmail);
        Assert.Equal(TicketStatus.InReview, row.Status);
        Assert.Equal("Billing", row.Category);
        Assert.Equal("Double charge.", row.Summary);
        Assert.Equal(0.9, row.Confidence);
        Assert.Equal(new TicketAssignee(AliceId, "Alice"), row.Assignee);
        Assert.True(row.HasDraft);
    }

    [Fact]
    public async Task ListAsync_UnclassifiedUndraftedTicket_HasNullsAndNoDraft()
    {
        var ticket = AddTicket(draft: null);
        ticket.Classification = null;

        var row = Assert.Single((await Create().ListAsync(AliceCaller, TicketFilter.Queue)).Value!);

        Assert.Null(row.Category);
        Assert.Null(row.Summary);
        Assert.Null(row.Confidence);
        Assert.Null(row.Assignee);
        Assert.False(row.HasDraft);
    }

    // ---- Get ----

    [Fact]
    public async Task GetAsync_UnknownTicket_IsNotFound()
    {
        var result = await Create().GetAsync(Guid.NewGuid());

        Assert.Equal(TicketOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetAsync_CustomerHtmlIsReturnedAsInertPlainText_AndAgentTextIsUnmodified()
    {
        var ticket = AddTicket(messages:
        [
            Customer("<p>Hello <b>there</b></p><script>alert('x')</script>", DateTimeOffset.UtcNow.AddMinutes(-20)),
            AgentMessage("Use a < b and <tag> literally.", DateTimeOffset.UtcNow.AddMinutes(-10)),
        ]);

        var detail = (await Create().GetAsync(ticket.Id)).Value!;

        Assert.Equal("Hello there", detail.Messages[0].BodyText);
        Assert.DoesNotContain("script", detail.Messages[0].BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", detail.Messages[0].BodyText);
        Assert.Equal("Use a < b and <tag> literally.", detail.Messages[1].BodyText);
    }

    [Fact]
    public async Task GetAsync_MessagesAreOldestFirst_AndCarryTheDraft()
    {
        var newer = Customer("second", DateTimeOffset.UtcNow.AddMinutes(-5), "ext-2");
        var older = Customer("first", DateTimeOffset.UtcNow.AddMinutes(-15), "ext-1");
        var ticket = AddTicket(draft: "AI draft", messages: [newer, older]);

        var detail = (await Create().GetAsync(ticket.Id)).Value!;

        Assert.Equal(["first", "second"], detail.Messages.Select(m => m.BodyText));
        Assert.Equal("AI draft", detail.DraftReply);
    }

    // ---- Claim ----

    [Fact]
    public async Task ClaimAsync_UnassignedTicket_AssignsToTheCaller()
    {
        var ticket = AddTicket();

        var result = await Create().ClaimAsync(AliceCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(AliceId, result.Value!.Assignee!.Id);
        Assert.Equal(AliceId, ticket.AssignedUserId);
    }

    [Fact]
    public async Task ClaimAsync_AlreadyMine_IsAnIdempotentSuccess()
    {
        var ticket = AddTicket(assignedTo: _alice);

        var result = await Create().ClaimAsync(AliceCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(AliceId, ticket.AssignedUserId);
    }

    [Fact]
    public async Task ClaimAsync_HeldBySomeoneElse_IsAConflictNamingTheHolder()
    {
        var ticket = AddTicket(assignedTo: _bob);

        var result = await Create().ClaimAsync(AliceCaller, ticket.Id);

        Assert.Equal(TicketOutcome.Conflict, result.Outcome);
        Assert.Contains("Bob", result.Message);
        Assert.Equal(BobId, ticket.AssignedUserId);
    }

    [Fact]
    public async Task ClaimAsync_UnknownTicket_IsNotFound()
    {
        var result = await Create().ClaimAsync(AliceCaller, Guid.NewGuid());

        Assert.Equal(TicketOutcome.NotFound, result.Outcome);
    }

    // ---- Release ----

    [Fact]
    public async Task ReleaseAsync_ByTheAssignee_ClearsTheAssignee()
    {
        var ticket = AddTicket(assignedTo: _alice);

        var result = await Create().ReleaseAsync(AliceCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(ticket.AssignedUserId);
        Assert.Null(result.Value!.Assignee);
    }

    [Fact]
    public async Task ReleaseAsync_ByAnAdmin_ReleasesSomeoneElsesTicket()
    {
        var ticket = AddTicket(assignedTo: _bob);

        var result = await Create().ReleaseAsync(AdminCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(ticket.AssignedUserId);
    }

    [Fact]
    public async Task ReleaseAsync_ByAnotherAgent_IsForbiddenAndChangesNothing()
    {
        var ticket = AddTicket(assignedTo: _bob);

        var result = await Create().ReleaseAsync(AliceCaller, ticket.Id);

        Assert.Equal(TicketOutcome.Forbidden, result.Outcome);
        Assert.Equal(BobId, ticket.AssignedUserId);
    }

    [Fact]
    public async Task ReleaseAsync_UnassignedTicket_IsANoOpSuccess()
    {
        var ticket = AddTicket();

        var result = await Create().ReleaseAsync(AliceCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(ticket.AssignedUserId);
    }

    [Fact]
    public async Task ReleaseAsync_UnknownTicket_IsNotFound()
    {
        var result = await Create().ReleaseAsync(AliceCaller, Guid.NewGuid());

        Assert.Equal(TicketOutcome.NotFound, result.Outcome);
    }

    // ---- Reply ----

    [Fact]
    public async Task SendReplyAsync_ByTheAssignee_SendsStoresAndMarksReplied()
    {
        var ticket = AddReplyableTicket(_alice);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "  Hello,\nWe fixed it.  ");

        Assert.True(result.IsSuccess);
        var (replyTo, text) = Assert.Single(_mail.SentReplies);
        Assert.Equal("ext-1", replyTo);
        Assert.Equal("Hello,\nWe fixed it.", text);

        Assert.Equal(TicketStatus.Replied, ticket.Status);
        Assert.Equal("AI draft", ticket.DraftReply);
        var stored = ticket.Messages.Single(m => !m.IsFromUser);
        Assert.Equal("Hello,\nWe fixed it.", stored.Body);
        Assert.Equal("alice@example.com", stored.Sender);
        Assert.Null(stored.ExternalMessageId);
        Assert.Equal(TicketStatus.Replied, result.Value!.Status);
        Assert.Equal(2, result.Value.Messages.Count);
    }

    [Fact]
    public async Task SendReplyAsync_RepliesToTheLatestCustomerMessageThatHasAnExternalId()
    {
        var ticket = AddTicket(assignedTo: _alice, messages:
        [
            Customer("old", DateTimeOffset.UtcNow.AddHours(-3), "ext-old"),
            Customer("newest but no id", DateTimeOffset.UtcNow.AddMinutes(-5), externalId: null),
            AgentMessage("earlier agent reply", DateTimeOffset.UtcNow.AddMinutes(-2)),
            Customer("newer with id", DateTimeOffset.UtcNow.AddHours(-1), "ext-new"),
        ]);

        await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal("ext-new", Assert.Single(_mail.SentReplies).ReplyToExternalMessageId);
    }

    [Fact]
    public async Task SendReplyAsync_NoEligibleCustomerMessage_IsInvalidAndSendsNothing()
    {
        var ticket = AddTicket(assignedTo: _alice, messages:
        [
            AgentMessage("only an agent message", DateTimeOffset.UtcNow.AddMinutes(-10)),
            Customer("no id", DateTimeOffset.UtcNow.AddMinutes(-20), externalId: null),
        ]);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.Invalid, result.Outcome);
        Assert.Equal(0, _mail.SendAttempts);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
    }

    [Fact]
    public async Task SendReplyAsync_UnknownTicket_IsNotFound()
    {
        var result = await Create().SendReplyAsync(AliceCaller, Guid.NewGuid(), "Reply");

        Assert.Equal(TicketOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task SendReplyAsync_UnassignedTicket_IsAConflictAndSendsNothing()
    {
        var ticket = AddReplyableTicket(assignedTo: null);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.Conflict, result.Outcome);
        Assert.Equal(0, _mail.SendAttempts);
    }

    [Fact]
    public async Task SendReplyAsync_ByAnotherAgent_IsForbiddenAndSendsNothing()
    {
        var ticket = AddReplyableTicket(_bob);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.Forbidden, result.Outcome);
        Assert.Equal(0, _mail.SendAttempts);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
    }

    [Fact]
    public async Task SendReplyAsync_ByAnAdmin_CanReplyOnSomeoneElsesClaimedTicket()
    {
        var ticket = AddReplyableTicket(_bob);

        var result = await Create().SendReplyAsync(AdminCaller, ticket.Id, "Reply");

        Assert.True(result.IsSuccess);
        Assert.Equal("admin@example.com", ticket.Messages.Single(m => !m.IsFromUser).Sender);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task SendReplyAsync_BlankText_IsInvalidAndSendsNothing(string text)
    {
        var ticket = AddReplyableTicket(_alice);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, text);

        Assert.Equal(TicketOutcome.Invalid, result.Outcome);
        Assert.Equal(0, _mail.SendAttempts);
    }

    [Fact]
    public async Task SendReplyAsync_TextAtTheLimit_IsSent_AndOneOverIsInvalid()
    {
        var ticket = AddReplyableTicket(_alice);
        var service = Create();

        var over = await service.SendReplyAsync(AliceCaller, ticket.Id, new string('x', TicketWorkflowService.MaxReplyLength + 1));
        Assert.Equal(TicketOutcome.Invalid, over.Outcome);
        Assert.Equal(0, _mail.SendAttempts);

        var atLimit = await service.SendReplyAsync(AliceCaller, ticket.Id, new string('x', TicketWorkflowService.MaxReplyLength));
        Assert.True(atLimit.IsSuccess);
    }

    [Fact]
    public async Task SendReplyAsync_GraphFails_IsSendFailedAndNothingIsStored()
    {
        var ticket = AddReplyableTicket(_alice);
        _mail.SendException = new HttpRequestException("graph down");

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.SendFailed, result.Outcome);
        Assert.Equal(1, _mail.SendAttempts);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
        Assert.Single(ticket.Messages);
    }

    [Fact]
    public async Task SendReplyAsync_SentButPersistenceFails_SaysTheEmailWasSent()
    {
        var ticket = AddReplyableTicket(_alice);
        _tickets.RecordReplyException = new InvalidOperationException("db down");

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.SentButNotSaved, result.Outcome);
        Assert.Contains("sent", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_mail.SentReplies);
    }

    [Fact]
    public async Task SendReplyAsync_NoMailClientConfigured_IsSendFailed()
    {
        var ticket = AddReplyableTicket(_alice);

        var result = await Create(withMailClient: false).SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.SendFailed, result.Outcome);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
    }

    [Fact]
    public async Task SendReplyAsync_AllowedOnARepliedTicket_AsAFollowUp()
    {
        var ticket = AddReplyableTicket(_alice, TicketStatus.Replied);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Follow-up");

        Assert.True(result.IsSuccess);
        Assert.Equal(TicketStatus.Replied, ticket.Status);
        Assert.Single(_mail.SentReplies);
    }

    [Fact]
    public async Task SendReplyAsync_CancelledWhileTokenCancelled_Propagates()
    {
        var ticket = AddReplyableTicket(_alice);
        _mail.SendException = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply", cts.Token));
    }
}
