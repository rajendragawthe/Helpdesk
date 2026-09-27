using Helpdesk.Core.Entities;

namespace Helpdesk.Core.Interfaces;

public interface IClassificationRepository
{
    Task AddAsync(Classification classification);

    /// <summary>
    /// Updates only Category, Summary and Confidence for an existing classification via a targeted
    /// UPDATE (CreatedAt is left as originally set). Used by reclassification so it never touches the
    /// Tickets table at all.
    /// </summary>
    Task UpdateAsync(Guid classificationId, string category, string summary, double confidence);
}
