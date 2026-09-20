using Helpdesk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Helpdesk.Infrastructure.Data.Configurations;

public class ClassificationConfiguration : IEntityTypeConfiguration<Classification>
{
    public void Configure(EntityTypeBuilder<Classification> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Category).IsRequired().HasMaxLength(100);
        builder.Property(c => c.Summary).IsRequired();
    }
}
