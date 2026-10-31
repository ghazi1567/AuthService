using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class TenantApplicationSubscriptionConfiguration : IEntityTypeConfiguration<TenantApplicationSubscription>
{
    public const string UniqueTenantApplicationIndexName = "ux_tenant_application_subscriptions_tenant_id_application_id";

    public void Configure(EntityTypeBuilder<TenantApplicationSubscription> builder)
    {
        builder.HasKey(subscription => subscription.Id);
        builder.Property(subscription => subscription.Id).ValueGeneratedNever();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(subscription => subscription.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Application>().WithMany().HasForeignKey(subscription => subscription.ApplicationId).OnDelete(DeleteBehavior.Restrict);

        // One subscription per tenant and product, however many times it is requested.
        builder.HasIndex(subscription => new { subscription.TenantId, subscription.ApplicationId })
            .IsUnique()
            .HasDatabaseName(UniqueTenantApplicationIndexName);

        builder.Property(subscription => subscription.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(subscription => subscription.MaxSeats);
        builder.ToTable(table => table.HasCheckConstraint("ck_tenant_application_subscriptions_max_seats", "max_seats IS NULL OR max_seats >= 1"));

        builder.Property(subscription => subscription.CreatedAt).IsRequired();
        builder.Property(subscription => subscription.UpdatedAt).IsRequired();
    }
}
