using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.OccurredAt).IsRequired();
        builder.Property(entry => entry.Action).HasMaxLength(64).IsRequired();
        builder.Property(entry => entry.ActorType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(entry => entry.ActorId);
        builder.Property(entry => entry.TenantId);
        builder.Property(entry => entry.SourceIp).HasMaxLength(45);
        builder.Property(entry => entry.Detail).HasMaxLength(128);

        // No foreign keys on purpose: an audit entry must outlive whatever it refers to.
        builder.HasIndex(entry => entry.OccurredAt);
        builder.HasIndex(entry => new { entry.TenantId, entry.OccurredAt });
        builder.HasIndex(entry => new { entry.ActorId, entry.OccurredAt });
    }
}
