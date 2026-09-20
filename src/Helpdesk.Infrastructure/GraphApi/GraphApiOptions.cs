namespace Helpdesk.Infrastructure.GraphApi;

public record GraphApiOptions(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string MailboxAddress)
{
    // Deliberately omits ClientSecret so it never leaks via ToString()/string interpolation
    // (records generate a ToString() that would otherwise print every property, including secrets).
    public override string ToString() =>
        $"GraphApiOptions {{ TenantId = {TenantId}, ClientId = {ClientId}, MailboxAddress = {MailboxAddress} }}";
}
