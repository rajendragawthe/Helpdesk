using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeMailClient : IMailClient
{
    private readonly List<InboundEmailMessage> _messages;

    public List<string> MarkedAsProcessed { get; } = [];

    public FakeMailClient(params InboundEmailMessage[] messages)
    {
        _messages = [.. messages];
    }

    public Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<InboundEmailMessage>>(_messages);
    }

    public Task MarkAsProcessedAsync(string externalMessageId, CancellationToken cancellationToken = default)
    {
        MarkedAsProcessed.Add(externalMessageId);
        return Task.CompletedTask;
    }

    public List<(string ReplyToExternalMessageId, string Text)> SentReplies { get; } = [];
    public int SendAttempts { get; private set; }
    public Exception? SendException { get; set; }

    public Task SendReplyAsync(string replyToExternalMessageId, string plainTextBody, CancellationToken cancellationToken = default)
    {
        SendAttempts++;
        if (SendException is not null)
        {
            throw SendException;
        }

        SentReplies.Add((replyToExternalMessageId, plainTextBody));
        return Task.CompletedTask;
    }
}
