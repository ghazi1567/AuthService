using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.HasKey(membership => membership.Id);
        builder.Property(membership => membership.Id).ValueGeneratedNever();

        // (TenantId, Id) is a second key so MembershipRole can reference both and prove its tenant matches.
        builder.HasAlternateKey(membership => new { membership.TenantId, membership.Id });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(membership => membership.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(membership => membership.UserId).OnDelete(DeleteBehavior.Restrict);

        // A person has at most one membership per tenant.
        builder.HasIndex(membership => new { membership.TenantId, membership.UserId }).IsUnique();

        builder.Property(membership => membership.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(membership => membership.CreatedAt).IsRequired();
    }
}
