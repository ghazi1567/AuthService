using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class WebhookOutboxMessageConfiguration : IEntityTypeConfiguration<WebhookOutboxMessage>
{
    public void Configure(EntityTypeBuilder<WebhookOutboxMessage> builder)
    {
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();

        builder.HasOne<Application>().WithMany().HasForeignKey(message => message.ApplicationId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(message => message.EventType).HasMaxLength(50).IsRequired();
        builder.Property(message => message.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(message => message.Attempts).IsRequired();
        builder.Property(message => message.NextAttemptAt).IsRequired();
        builder.Property(message => message.LastError).HasMaxLength(500);
        builder.Property(message => message.CreatedAt).IsRequired();

        // The delivery worker looks for pending messages that are due.
        builder.HasIndex(message => new { message.Status, message.NextAttemptAt });
    }
}
