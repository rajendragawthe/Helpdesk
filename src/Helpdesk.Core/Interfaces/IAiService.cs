using Helpdesk.Core.Models;

namespace Helpdesk.Core.Interfaces;

public interface IAiService
{
    /// <summary>
    /// Classifies a support email and summarizes it in one call. <paramref name="body"/> is plain text.
    /// Throws if the provider is unreachable or returns an unusable response.
    /// </summary>
    Task<ClassificationResult> ClassifyAsync(string subject, string body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drafts a plain-text reply to a support email for an agent to review. <paramref name="body"/> is plain
    /// text; <paramref name="articles"/> are the matched knowledge base articles (possibly empty). The draft is
    /// trimmed and bounded. Throws if the provider is unreachable or returns an unusable (e.g. blank) response.
    /// </summary>
    Task<string> DraftReplyAsync(
        string subject,
        string body,
        string? category,
        IReadOnlyList<KbArticle> articles,
        CancellationToken cancellationToken = default);
}
