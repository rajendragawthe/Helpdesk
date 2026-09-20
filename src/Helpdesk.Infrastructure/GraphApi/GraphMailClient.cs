using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Helpdesk.Infrastructure.GraphApi;

public class GraphMailClient(GraphServiceClient graphClient, GraphApiOptions options, ILogger<GraphMailClient> logger) : IMailClient
{
    public async Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(CancellationToken cancellationToken = default)
    {
        var response = await graphClient.Users[options.MailboxAddress]
            .MailFolders["Inbox"]
            .Messages
            .GetAsync(requestConfig =>
            {
                requestConfig.QueryParameters.Filter = "isRead eq false";
                requestConfig.QueryParameters.Orderby = ["receivedDateTime asc"];
                requestConfig.QueryParameters.Select =
                    ["id", "conversationId", "from", "subject", "body", "receivedDateTime"];
                requestConfig.QueryParameters.Top = 50;
            }, cancellationToken);

        var messages = response?.Value ?? [];

        foreach (var malformed in messages.Where(m => m.Id is null || m.ConversationId is null))
        {
            logger.LogWarning(
                "Skipping malformed Graph message missing Id or ConversationId. Subject={Subject}, ReceivedDateTime={ReceivedDateTime}",
                malformed.Subject,
                malformed.ReceivedDateTime);
        }

        return messages
            .Where(m => m.Id is not null && m.ConversationId is not null)
            .Select(m => new InboundEmailMessage(
                ExternalMessageId: m.Id!,
                ConversationId: m.ConversationId!,
                FromAddress: m.From?.EmailAddress?.Address ?? "unknown@unknown",
                Subject: m.Subject ?? "(no subject)",
                BodyHtml: m.Body?.Content ?? string.Empty,
                ReceivedAt: m.ReceivedDateTime ?? DateTimeOffset.UtcNow))
            .ToList();
    }

    public async Task MarkAsProcessedAsync(string externalMessageId, CancellationToken cancellationToken = default)
    {
        await graphClient.Users[options.MailboxAddress]
            .Messages[externalMessageId]
            .PatchAsync(new Message { IsRead = true }, cancellationToken: cancellationToken);
    }
}
