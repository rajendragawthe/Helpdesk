using Helpdesk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Helpdesk.Infrastructure.Data.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Sender).IsRequired().HasMaxLength(320);
        builder.Property(m => m.Body).IsRequired();
        builder.Property(m => m.ExternalMessageId).HasMaxLength(200);
        builder.HasIndex(m => m.ExternalMessageId);
    }
}
