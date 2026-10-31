using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class EmailOutboxMessageConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> builder)
    {
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();

        builder.Property(message => message.ToAddress).HasMaxLength(254).IsRequired();
        builder.Property(message => message.Template).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(message => message.PayloadJson);
        builder.Property(message => message.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(message => message.Attempts).IsRequired();
        builder.Property(message => message.NextAttemptAt).IsRequired();
        builder.Property(message => message.LastError).HasMaxLength(500);
        builder.Property(message => message.CreatedAt).IsRequired();

        // The worker looks for pending messages that are due.
        builder.HasIndex(message => new { message.Status, message.NextAttemptAt });
    }
}
