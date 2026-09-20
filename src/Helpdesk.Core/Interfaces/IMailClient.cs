using Helpdesk.Core.Models;

namespace Helpdesk.Core.Interfaces;

public interface IMailClient
{
    Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(CancellationToken cancellationToken = default);

    Task MarkAsProcessedAsync(string externalMessageId, CancellationToken cancellationToken = default);
}
