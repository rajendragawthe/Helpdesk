using System.Text.RegularExpressions;
using Helpdesk.Core.Models;

namespace Helpdesk.Application.DraftReply;

/// <summary>
/// Picks the KB articles most relevant to a ticket: hand-authored keywords found as whole words/phrases
/// in subject + body (each distinct keyword counts once), +1 when the article's category equals the
/// ticket's. Articles with no keyword hit are never selected, however well the category matches.
/// </summary>
public static class KbMatcher
{
    public const int MaxArticles = 3;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<KbArticle> Match(
        string subject, string body, string? category, IReadOnlyList<KbArticle> articles)
    {
        var text = subject + "\n" + body;

        return articles
            .Select((article, index) => (Article: article, Index: index, Hits: CountHits(article, text)))
            .Where(x => x.Hits > 0)
            .Select(x => (x.Article, x.Index, Score: x.Hits + (IsCategoryMatch(x.Article, category) ? 1 : 0)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(MaxArticles)
            .Select(x => x.Article)
            .ToList();
    }

    private static bool IsCategoryMatch(KbArticle article, string? category) =>
        category is not null && string.Equals(article.Category, category, StringComparison.OrdinalIgnoreCase);

    private static int CountHits(KbArticle article, string text) =>
        article.Keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(k => ContainsWholePhrase(text, k));

    private static bool ContainsWholePhrase(string text, string phrase) =>
        Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout);
}
