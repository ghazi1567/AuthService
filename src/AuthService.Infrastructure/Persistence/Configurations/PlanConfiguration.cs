using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public const string UniqueKeyPerApplicationIndexName = "ux_plans_application_id_key";

    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.HasKey(plan => plan.Id);
        builder.Property(plan => plan.Id).ValueGeneratedNever();

        builder.HasOne<Application>().WithMany().HasForeignKey(plan => plan.ApplicationId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(plan => plan.Key).HasMaxLength(100).IsRequired();
        builder.HasIndex(plan => new { plan.ApplicationId, plan.Key })
            .IsUnique()
            .HasDatabaseName(UniqueKeyPerApplicationIndexName);

        builder.Property(plan => plan.Name).HasMaxLength(200).IsRequired();
        builder.Property(plan => plan.Description).HasMaxLength(1000);
        builder.Property(plan => plan.CreatedAt).IsRequired();
        builder.Property(plan => plan.UpdatedAt).IsRequired();
    }
}

public sealed class PlanValueConfiguration : IEntityTypeConfiguration<PlanValue>
{
    public void Configure(EntityTypeBuilder<PlanValue> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();

        // A plan's values go with it; a limit cannot be deleted at all, so restrict is only a guard.
        builder.HasOne<Plan>().WithMany().HasForeignKey(value => value.PlanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LimitDefinition>().WithMany().HasForeignKey(value => value.LimitDefinitionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(value => new { value.PlanId, value.LimitDefinitionId }).IsUnique();
        builder.ToTable(table => table.HasCheckConstraint("ck_plan_values_value_not_negative", "value IS NULL OR value >= 0"));
    }
}
