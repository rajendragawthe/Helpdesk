using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeAiService : IAiService
{
    public record DraftCall(string Subject, string Body, string? Category, IReadOnlyList<KbArticle> Articles);

    public ClassificationResult Result { get; set; } = new("Billing", "Customer disputes a charge.", 0.9);
    public Exception? ExceptionToThrow { get; set; }
    public List<(string Subject, string Body)> Calls { get; } = [];

    public string DraftResult { get; set; } = "Hello,\n\nThanks for getting in touch.\n\nThe Support Team";
    public Exception? DraftExceptionToThrow { get; set; }
    public List<DraftCall> DraftCalls { get; } = [];

    public Task<ClassificationResult> ClassifyAsync(string subject, string body, CancellationToken cancellationToken = default)
    {
        Calls.Add((subject, body));
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(Result);
    }

    public Task<string> DraftReplyAsync(
        string subject,
        string body,
        string? category,
        IReadOnlyList<KbArticle> articles,
        CancellationToken cancellationToken = default)
    {
        DraftCalls.Add(new DraftCall(subject, body, category, articles));
        if (DraftExceptionToThrow is not null)
        {
            throw DraftExceptionToThrow;
        }

        return Task.FromResult(DraftResult);
    }
}
