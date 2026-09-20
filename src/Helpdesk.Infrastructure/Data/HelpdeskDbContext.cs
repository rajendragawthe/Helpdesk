using Helpdesk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Data;

public class HelpdeskDbContext(DbContextOptions<HelpdeskDbContext> options) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Classification> Classifications => Set<Classification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HelpdeskDbContext).Assembly);
    }
}
