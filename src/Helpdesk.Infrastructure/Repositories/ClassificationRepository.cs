using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Data;

namespace Helpdesk.Infrastructure.Repositories;

public class ClassificationRepository(HelpdeskDbContext dbContext) : IClassificationRepository
{
    public async Task AddAsync(Classification classification)
    {
        dbContext.Classifications.Add(classification);
        await dbContext.SaveChangesAsync();
    }
}
