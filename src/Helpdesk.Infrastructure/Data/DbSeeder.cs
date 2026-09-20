using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(HelpdeskDbContext dbContext, bool isDevelopment)
    {
        await SeedBootstrapAdminAsync(dbContext);

        if (!isDevelopment)
        {
            return;
        }

        await SeedDevTestAccountsAsync(dbContext);
        await SeedDemoTicketsAsync(dbContext);
    }

    private static async Task SeedBootstrapAdminAsync(HelpdeskDbContext dbContext)
    {
        if (await dbContext.Users.AnyAsync(u => u.Role == Role.Admin))
        {
            return;
        }

        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = "rajendra.gawthe@ProsaresSolutions.onmicrosoft.com",
            DisplayName = "Rajendra Gawthe",
            Role = Role.Admin,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedDevTestAccountsAsync(HelpdeskDbContext dbContext)
    {
        var existingEmails = await dbContext.Users
            .Select(u => u.Email)
            .ToListAsync();
        var existing = new HashSet<string>(existingEmails, StringComparer.OrdinalIgnoreCase);

        var candidates = new[]
        {
            new User
            {
                Id = Guid.NewGuid(),
                Email = "sarah.connor@ProsaresSolutions.onmicrosoft.com",
                DisplayName = "Sarah Connor",
                Role = Role.Agent,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new User
            {
                Id = Guid.NewGuid(),
                Email = "epm1@prosaressolutions.onmicrosoft.com",
                DisplayName = "Admin User 1",
                Role = Role.Admin,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new User
            {
                Id = Guid.NewGuid(),
                Email = "epm2@prosaressolutions.onmicrosoft.com",
                DisplayName = "Agent User1",
                Role = Role.Agent,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        var toAdd = candidates.Where(c => !existing.Contains(c.Email)).ToList();
        if (toAdd.Count > 0)
        {
            dbContext.Users.AddRange(toAdd);
            await dbContext.SaveChangesAsync();
        }
    }

    private static async Task SeedDemoTicketsAsync(HelpdeskDbContext dbContext)
    {
        if (await dbContext.Tickets.AnyAsync())
        {
            return;
        }

        var agentUser = await dbContext.Users.FirstAsync(u => u.Role == Role.Agent);
        var now = DateTimeOffset.UtcNow;

        var ticket1 = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Cannot reset my password",
            RequesterEmail = "customer1@example.com",
            Status = TicketStatus.New,
            ConversationId = "conv-0001",
            CreatedAt = now.AddHours(-3),
            UpdatedAt = now.AddHours(-3)
        };
        ticket1.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            TicketId = ticket1.Id,
            Sender = "customer1@example.com",
            Body = "I tried the 'forgot password' link but never received the reset email.",
            IsFromUser = false,
            ExternalMessageId = "msg-0001",
            ReceivedAt = now.AddHours(-3)
        });

        var ticket2 = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Invoice amount looks incorrect",
            RequesterEmail = "customer2@example.com",
            Status = TicketStatus.InReview,
            ConversationId = "conv-0002",
            DraftReply = "Hi, thanks for flagging this - I've reviewed invoice #4521 and you're right, a discount wasn't applied. I'm issuing a corrected invoice now.",
            AssignedUserId = agentUser.Id,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddHours(-1)
        };
        ticket2.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            TicketId = ticket2.Id,
            Sender = "customer2@example.com",
            Body = "My latest invoice #4521 seems higher than usual, can you check it?",
            IsFromUser = false,
            ExternalMessageId = "msg-0002",
            ReceivedAt = now.AddDays(-1)
        });
        ticket2.Classification = new Classification
        {
            Id = Guid.NewGuid(),
            TicketId = ticket2.Id,
            Category = "Billing",
            Summary = "Customer disputes an amount on invoice #4521.",
            Confidence = 0.87,
            CreatedAt = now.AddHours(-20)
        };

        var ticket3 = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Feature request: dark mode",
            RequesterEmail = "customer3@example.com",
            Status = TicketStatus.Replied,
            ConversationId = "conv-0003",
            AssignedUserId = agentUser.Id,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2).AddHours(4)
        };
        ticket3.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            TicketId = ticket3.Id,
            Sender = "customer3@example.com",
            Body = "Would love a dark mode option in the dashboard.",
            IsFromUser = false,
            ExternalMessageId = "msg-0003",
            ReceivedAt = now.AddDays(-2)
        });
        ticket3.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            TicketId = ticket3.Id,
            Sender = agentUser.Email,
            Body = "Thanks for the suggestion! I've logged this with our product team for consideration.",
            IsFromUser = true,
            ReceivedAt = now.AddDays(-2).AddHours(4)
        });
        ticket3.Classification = new Classification
        {
            Id = Guid.NewGuid(),
            TicketId = ticket3.Id,
            Category = "Feature Request",
            Summary = "Customer requests a dark mode UI option.",
            Confidence = 0.95,
            CreatedAt = now.AddDays(-2).AddMinutes(5)
        };

        dbContext.Tickets.AddRange(ticket1, ticket2, ticket3);
        await dbContext.SaveChangesAsync();
    }
}
