namespace Helpdesk.Core.Models;

public sealed record KbArticle(
    string Id,
    string Title,
    string Category,
    IReadOnlyList<string> Keywords,
    string Content);
