using Helpdesk.Core.Interfaces;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeClassificationRepository : IClassificationRepository
{
    public List<Helpdesk.Core.Entities.Classification> Classifications { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    public Task AddAsync(Helpdesk.Core.Entities.Classification classification)
    {
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        Classifications.Add(classification);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Guid classificationId, string category, string summary, double confidence)
    {
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        var classification = Classifications.FirstOrDefault(c => c.Id == classificationId);
        if (classification is not null)
        {
            classification.Category = category;
            classification.Summary = summary;
            classification.Confidence = confidence;
        }

        return Task.CompletedTask;
    }
}
