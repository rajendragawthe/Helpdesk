using Helpdesk.Core.Models;

namespace Helpdesk.Core.Interfaces;

public interface IMailClient
{
    Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(CancellationToken cancellationToken = default);

    Task MarkAsProcessedAsync(string externalMessageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a plain-text reply on the mail thread of the given inbound message (Graph reply), so it threads
    /// with the customer's email. Throws if the provider rejects or cannot be reached.
    /// </summary>
    Task SendReplyAsync(string replyToExternalMessageId, string plainTextBody, CancellationToken cancellationToken = default);
}
