using Helpdesk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Helpdesk.Infrastructure.Data.Configurations;

public class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Subject).IsRequired().HasMaxLength(1000);
        builder.Property(t => t.RequesterEmail).IsRequired().HasMaxLength(400);
        builder.Property(t => t.ConversationId).HasMaxLength(200);
        builder.HasIndex(t => t.ConversationId);

        builder.HasOne(t => t.AssignedUser)
            .WithMany()
            .HasForeignKey(t => t.AssignedUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(t => t.Classification)
            .WithOne(c => c.Ticket)
            .HasForeignKey<Classification>(c => c.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Messages)
            .WithOne(m => m.Ticket)
            .HasForeignKey(m => m.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
