namespace Helpdesk.Infrastructure.GraphApi;

public record GraphApiOptions(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string MailboxAddress,
    int PollingIntervalSeconds);
