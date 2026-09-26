namespace Helpdesk.Core.Models;

public static class TicketCategories
{
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } =
    [
        "Billing",
        "Technical Issue",
        "Account Access",
        "Feature Request",
        "General Inquiry",
        Other,
    ];

    public static string Normalize(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return Other;
        }

        var trimmed = category.Trim();
        return All.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase)) ?? Other;
    }
}
