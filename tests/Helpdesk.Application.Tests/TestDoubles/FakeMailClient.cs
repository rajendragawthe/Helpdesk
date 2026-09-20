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
}
