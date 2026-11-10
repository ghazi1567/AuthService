using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class SubscriptionOverrideConfiguration : IEntityTypeConfiguration<SubscriptionOverride>
{
    public void Configure(EntityTypeBuilder<SubscriptionOverride> builder)
    {
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.HasOne<TenantApplicationSubscription>().WithMany().HasForeignKey(entry => entry.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LimitDefinition>().WithMany().HasForeignKey(entry => entry.LimitDefinitionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entry => new { entry.SubscriptionId, entry.LimitDefinitionId }).IsUnique();

        builder.Property(entry => entry.Reason).HasMaxLength(200).IsRequired();
        builder.Property(entry => entry.SetByType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.ToTable(table => table.HasCheckConstraint("ck_subscription_overrides_value_not_negative", "value IS NULL OR value >= 0"));
    }
}

public sealed class EntitlementChangeConfiguration : IEntityTypeConfiguration<EntitlementChange>
{
    public void Configure(EntityTypeBuilder<EntitlementChange> builder)
    {
        builder.HasKey(change => change.Id);
        builder.Property(change => change.Id).ValueGeneratedNever();

        // No foreign keys: the history outlives what it describes (a deleted plan, a deleted tenant) and only ever grows.
        builder.Property(change => change.Change).HasMaxLength(40).IsRequired();
        builder.Property(change => change.PlanKey).HasMaxLength(100);
        builder.Property(change => change.LimitKey).HasMaxLength(100);
        builder.Property(change => change.OldValue).HasColumnType("jsonb");
        builder.Property(change => change.NewValue).HasColumnType("jsonb");
        builder.Property(change => change.Reason).HasMaxLength(200);
        builder.Property(change => change.ActorType).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(change => new { change.ApplicationId, change.OccurredAt });
        builder.HasIndex(change => new { change.TenantId, change.OccurredAt });
    }
}
