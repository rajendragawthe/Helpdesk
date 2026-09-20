using Helpdesk.Application.EmailIngestion;
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Application.Tests.EmailIngestion;

public class EmailIngestionServiceTests
{
    [Fact]
    public async Task IngestNewEmailsAsync_NewConversation_CreatesTicketAndMessage()
    {
        var email = new InboundEmailMessage(
            ExternalMessageId: "msg-1",
            ConversationId: "conv-1",
            FromAddress: "requester@example.com",
            Subject: "Help please",
            BodyHtml: "<p>I need help</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(email);
        var ticketRepository = new FakeTicketRepository();
        var messageRepository = new FakeMessageRepository();
        var scopeFactory = new FakeServiceScopeFactory(ticketRepository, messageRepository);
        var service = new EmailIngestionService(mailClient, scopeFactory, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        var ticket = Assert.Single(ticketRepository.Tickets);
        Assert.Equal("conv-1", ticket.ConversationId);
        Assert.Equal("requester@example.com", ticket.RequesterEmail);
        Assert.Equal(TicketStatus.New, ticket.Status);

        var message = Assert.Single(messageRepository.Messages);
        Assert.Equal(ticket.Id, message.TicketId);
        Assert.Equal("<p>I need help</p>", message.Body);
        Assert.True(message.IsFromUser);
        Assert.Equal("msg-1", message.ExternalMessageId);

        Assert.Equal(["msg-1"], mailClient.MarkedAsProcessed);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_ExistingConversation_AppendsMessageAndLeavesStatusUnchanged()
    {
        var existingTicket = new Helpdesk.Core.Entities.Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Original subject",
            RequesterEmail = "requester@example.com",
            Status = TicketStatus.Replied,
            ConversationId = "conv-1",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        var reply = new InboundEmailMessage(
            ExternalMessageId: "msg-2",
            ConversationId: "conv-1",
            FromAddress: "requester@example.com",
            Subject: "RE: Original subject",
            BodyHtml: "<p>Still broken</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(reply);
        var ticketRepository = new FakeTicketRepository();
        ticketRepository.Tickets.Add(existingTicket);
        var messageRepository = new FakeMessageRepository();
        var scopeFactory = new FakeServiceScopeFactory(ticketRepository, messageRepository);
        var service = new EmailIngestionService(mailClient, scopeFactory, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        Assert.Single(ticketRepository.Tickets);
        Assert.Equal(TicketStatus.Replied, existingTicket.Status);

        var message = Assert.Single(messageRepository.Messages);
        Assert.Equal(existingTicket.Id, message.TicketId);
        Assert.Equal("msg-2", message.ExternalMessageId);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_DuplicateExternalMessageId_IsSkipped()
    {
        var email = new InboundEmailMessage(
            ExternalMessageId: "msg-1",
            ConversationId: "conv-1",
            FromAddress: "requester@example.com",
            Subject: "Help please",
            BodyHtml: "<p>I need help</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(email);
        var ticketRepository = new FakeTicketRepository();
        var messageRepository = new FakeMessageRepository();
        messageRepository.Messages.Add(new Helpdesk.Core.Entities.Message
        {
            Id = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            Sender = "requester@example.com",
            Body = "<p>I need help</p>",
            IsFromUser = true,
            ExternalMessageId = "msg-1",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        var scopeFactory = new FakeServiceScopeFactory(ticketRepository, messageRepository);
        var service = new EmailIngestionService(mailClient, scopeFactory, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        Assert.Empty(ticketRepository.Tickets);
        Assert.Single(messageRepository.Messages);
        Assert.Empty(mailClient.MarkedAsProcessed);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_OneMessageFailsToPersist_OtherMessagesStillProcessed()
    {
        var failingEmail = new InboundEmailMessage(
            ExternalMessageId: "msg-fail",
            ConversationId: "conv-fail",
            FromAddress: "a@example.com",
            Subject: "Will fail",
            BodyHtml: "<p>boom</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var okEmail = new InboundEmailMessage(
            ExternalMessageId: "msg-ok",
            ConversationId: "conv-ok",
            FromAddress: "b@example.com",
            Subject: "Will succeed",
            BodyHtml: "<p>fine</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(failingEmail, okEmail);
        var ticketRepository = new FakeTicketRepository();
        var messageRepository = new FakeMessageRepository { ThrowOnAddForExternalMessageId = "msg-fail" };
        var scopeFactory = new FakeServiceScopeFactory(ticketRepository, messageRepository);
        var service = new EmailIngestionService(mailClient, scopeFactory, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        // Self-healing / eventual-consistency design: ticket creation for the failing
        // email succeeds and is legitimately left in place even though its message
        // failed to persist. The next poll tick will retry "msg-fail" (it was never
        // recorded as processed), find the already-created "conv-fail" ticket via
        // GetByConversationIdAsync, and append the message to it instead of creating
        // a duplicate ticket. No data loss, just a one-tick delay.
        Assert.Equal(2, ticketRepository.Tickets.Count);

        var failedTicket = Assert.Single(ticketRepository.Tickets, t => t.ConversationId == "conv-fail");
        Assert.DoesNotContain(messageRepository.Messages, m => m.TicketId == failedTicket.Id);

        var okTicket = Assert.Single(ticketRepository.Tickets, t => t.ConversationId == "conv-ok");
        var message = Assert.Single(messageRepository.Messages);
        Assert.Equal(okTicket.Id, message.TicketId);
        Assert.Equal("msg-ok", message.ExternalMessageId);

        Assert.Equal(["msg-ok"], mailClient.MarkedAsProcessed);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_FirstMessagePoisoned_SecondMessageStillProcessedInItsOwnScope()
    {
        // Regression test for the shared-DbContext-per-tick bug: previously
        // EmailIngestionService held ITicketRepository/IMessageRepository via constructor
        // injection, so every message in a tick shared the same repository/DbContext
        // instance. If the FIRST message's persistence failed, a poisoned change tracker
        // could block every subsequent message in the same tick from being saved too
        // (Graph returns unread messages oldest-first, so the poisoned message is always
        // the OLDEST and is never marked read/removed from the unread set).
        //
        // Now each message is processed in its own DI scope with its own repositories
        // (its own HelpdeskDbContext in production), so a failure processing the first
        // message cannot affect the second message's scope at all.
        var poisonedFirstEmail = new InboundEmailMessage(
            ExternalMessageId: "msg-poisoned-first",
            ConversationId: "conv-poisoned",
            FromAddress: "first@example.com",
            Subject: "First and poisoned",
            BodyHtml: "<p>boom</p>",
            ReceivedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var secondEmail = new InboundEmailMessage(
            ExternalMessageId: "msg-second",
            ConversationId: "conv-second",
            FromAddress: "second@example.com",
            Subject: "Second, should still succeed",
            BodyHtml: "<p>fine</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(poisonedFirstEmail, secondEmail);
        var ticketRepository = new FakeTicketRepository();
        var messageRepository = new FakeMessageRepository { ThrowOnAddForExternalMessageId = "msg-poisoned-first" };
        var scopeFactory = new FakeServiceScopeFactory(ticketRepository, messageRepository);
        var service = new EmailIngestionService(mailClient, scopeFactory, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        // Exactly two scopes were created - one per message - proving no two messages
        // in this tick shared a scope (and therefore, in production, no shared DbContext).
        Assert.Equal(2, scopeFactory.ScopesCreated);

        var secondTicket = Assert.Single(ticketRepository.Tickets, t => t.ConversationId == "conv-second");
        var secondMessage = Assert.Single(messageRepository.Messages);
        Assert.Equal(secondTicket.Id, secondMessage.TicketId);
        Assert.Equal("msg-second", secondMessage.ExternalMessageId);
        Assert.Equal(["msg-second"], mailClient.MarkedAsProcessed);
    }
}
