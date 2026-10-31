using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class MembershipApplicationAccessConfiguration : IEntityTypeConfiguration<MembershipApplicationAccess>
{
    public void Configure(EntityTypeBuilder<MembershipApplicationAccess> builder)
    {
        builder.HasKey(access => access.Id);
        builder.Property(access => access.Id).ValueGeneratedNever();

        // The foreign key includes TenantId, so the database rejects a membership from another tenant.
        builder.HasOne<TenantMembership>()
            .WithMany()
            .HasForeignKey(access => new { access.TenantId, access.MembershipId })
            .HasPrincipalKey(membership => new { membership.TenantId, membership.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Application>().WithMany().HasForeignKey(access => access.ApplicationId).OnDelete(DeleteBehavior.Restrict);

        // A member has at most one seat per product.
        builder.HasIndex(access => new { access.MembershipId, access.ApplicationId }).IsUnique();

        // Seats are counted per tenant and product.
        builder.HasIndex(access => new { access.TenantId, access.ApplicationId });

        builder.Property(access => access.GrantedAt).IsRequired();
    }
}
