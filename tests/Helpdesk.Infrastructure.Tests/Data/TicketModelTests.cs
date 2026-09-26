using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Helpdesk.Infrastructure.Tests.Data;

public class TicketModelTests
{
    // Only the model is inspected; nothing connects to the database.
    private static IProperty ReviewReasonsProperty()
    {
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var context = new HelpdeskDbContext(options);
        return context.Model.FindEntityType(typeof(Ticket))!.FindProperty(nameof(Ticket.ReviewReasons))!;
    }

    [Fact]
    public void ReviewReasons_IsARequiredColumnThatDefaultsToNone()
    {
        var property = ReviewReasonsProperty();

        Assert.Equal(typeof(ReviewReasons), property.ClrType);
        Assert.False(property.IsNullable);
        Assert.Equal(0, Convert.ToInt32(property.GetDefaultValue()));
    }
}
