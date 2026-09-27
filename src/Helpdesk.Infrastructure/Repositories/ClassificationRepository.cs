using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Repositories;

public class ClassificationRepository(HelpdeskDbContext dbContext) : IClassificationRepository
{
    public async Task AddAsync(Classification classification)
    {
        dbContext.Classifications.Add(classification);
        await dbContext.SaveChangesAsync();
    }

    public async Task UpdateAsync(Guid classificationId, string category, string summary, double confidence)
    {
        await dbContext.Classifications
            .Where(c => c.Id == classificationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Category, category)
                .SetProperty(c => c.Summary, summary)
                .SetProperty(c => c.Confidence, confidence));
    }
}
