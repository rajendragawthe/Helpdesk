using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeAiService : IAiService
{
    public ClassificationResult Result { get; set; } = new("Billing", "Customer disputes a charge.", 0.9);
    public Exception? ExceptionToThrow { get; set; }
    public List<(string Subject, string Body)> Calls { get; } = [];

    public Task<ClassificationResult> ClassifyAsync(string subject, string body, CancellationToken cancellationToken = default)
    {
        Calls.Add((subject, body));
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(Result);
    }
}
