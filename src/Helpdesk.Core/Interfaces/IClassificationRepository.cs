using Helpdesk.Core.Entities;

namespace Helpdesk.Core.Interfaces;

public interface IClassificationRepository
{
    Task AddAsync(Classification classification);
}
