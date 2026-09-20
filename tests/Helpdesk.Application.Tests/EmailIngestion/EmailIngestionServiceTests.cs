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
        var service = new EmailIngestionService(mailClient, ticketRepository, messageRepository, NullLogger<EmailIngestionService>.Instance);

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
        var service = new EmailIngestionService(mailClient, ticketRepository, messageRepository, NullLogger<EmailIngestionService>.Instance);

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
        var service = new EmailIngestionService(mailClient, ticketRepository, messageRepository, NullLogger<EmailIngestionService>.Instance);

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
        var service = new EmailIngestionService(mailClient, ticketRepository, messageRepository, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        Assert.Single(ticketRepository.Tickets);
        Assert.Equal("conv-ok", ticketRepository.Tickets[0].ConversationId);
        Assert.Single(messageRepository.Messages);
        Assert.Equal(["msg-ok"], mailClient.MarkedAsProcessed);
    }
}
