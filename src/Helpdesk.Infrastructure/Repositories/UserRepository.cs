using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Repositories;

public class UserRepository(HelpdeskDbContext dbContext) : IUserRepository
{
    public async Task<User?> GetByIdAsync(Guid id)
    {
        return await dbContext.Users.FirstOrDefaultAsync(u => u.Id == id);
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        return await dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
    }

    public async Task<User?> GetByExternalObjectIdAsync(string externalObjectId)
    {
        return await dbContext.Users.FirstOrDefaultAsync(u => u.ExternalObjectId == externalObjectId);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync()
    {
        return await dbContext.Users.OrderBy(u => u.DisplayName).ToListAsync();
    }

    public async Task AddAsync(User user)
    {
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
    }

    public async Task UpdateAsync(User user)
    {
        dbContext.Users.Update(user);
        await dbContext.SaveChangesAsync();
    }
}
