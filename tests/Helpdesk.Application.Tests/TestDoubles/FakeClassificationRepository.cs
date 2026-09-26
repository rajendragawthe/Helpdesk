using Helpdesk.Core.Entities;
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
}
