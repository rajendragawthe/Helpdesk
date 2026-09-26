using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;

namespace Helpdesk.Api.Tests.TestDoubles;

public class FakeUserRepository : IUserRepository
{
    public List<User> Users { get; } = [];

    public Task<User?> GetByIdAsync(Guid id) => Task.FromResult(Users.FirstOrDefault(u => u.Id == id));

    public Task<User?> GetByEmailAsync(string email) =>
        Task.FromResult(Users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task<User?> GetByExternalObjectIdAsync(string externalObjectId) =>
        Task.FromResult(Users.FirstOrDefault(u => u.ExternalObjectId == externalObjectId));

    public Task<IReadOnlyList<User>> GetAllAsync() => Task.FromResult<IReadOnlyList<User>>(Users);

    public Task AddAsync(User user)
    {
        Users.Add(user);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User user) => Task.CompletedTask;
}
