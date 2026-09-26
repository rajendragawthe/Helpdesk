using Helpdesk.Core.Models;

namespace Helpdesk.Core.Interfaces;

public interface IAiService
{
    /// <summary>
    /// Classifies a support email and summarizes it in one call. <paramref name="body"/> is plain text.
    /// Throws if the provider is unreachable or returns an unusable response.
    /// </summary>
    Task<ClassificationResult> ClassifyAsync(string subject, string body, CancellationToken cancellationToken = default);
}
