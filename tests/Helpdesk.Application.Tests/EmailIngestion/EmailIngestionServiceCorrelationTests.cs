using Helpdesk.Application.EmailIngestion;
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Core.Models;

namespace Helpdesk.Application.Tests.EmailIngestion;

public class EmailIngestionServiceCorrelationTests
{
    [Fact]
    public async Task IngestNewEmailsAsync_PushesExternalMessageIdAndConversationIdScope_PerEmail()
    {
        var emailOne = new InboundEmailMessage(
            ExternalMessageId: "msg-1",
            ConversationId: "conv-1",
            FromAddress: "one@example.com",
            Subject: "First",
            BodyHtml: "<p>First</p>",
            ReceivedAt: DateTimeOffset.UtcNow);
        var emailTwo = new InboundEmailMessage(
            ExternalMessageId: "msg-2",
            ConversationId: "conv-2",
            FromAddress: "two@example.com",
            Subject: "Second",
            BodyHtml: "<p>Second</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(emailOne, emailTwo);
        var ticketRepository = new FakeTicketRepository();
        var messageRepository = new FakeMessageRepository();
        var scopeFactory = new FakeServiceScopeFactory(ticketRepository, messageRepository);
        var logger = new RecordingLogger<EmailIngestionService>();
        var service = new EmailIngestionService(mailClient, scopeFactory, logger);

        await service.IngestNewEmailsAsync();

        Assert.Equal(2, logger.Scopes.Count);
        Assert.Equal("msg-1", logger.Scopes[0]["ExternalMessageId"]);
        Assert.Equal("conv-1", logger.Scopes[0]["ConversationId"]);
        Assert.Equal("msg-2", logger.Scopes[1]["ExternalMessageId"]);
        Assert.Equal("conv-2", logger.Scopes[1]["ConversationId"]);
    }
}
