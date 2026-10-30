using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Id).ValueGeneratedNever();

        // (TenantId, Id) is a second key so MembershipRole can reference both and prove its tenant matches.
        builder.HasAlternateKey(role => new { role.TenantId, role.Id });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(role => role.TenantId).OnDelete(DeleteBehavior.Restrict);

        // Role names are unique per tenant, not globally.
        builder.HasIndex(role => new { role.TenantId, role.Name }).IsUnique();

        builder.Property(role => role.Name).HasMaxLength(100).IsRequired();
        builder.Property(role => role.IsBuiltIn).IsRequired();
        builder.Property(role => role.CreatedAt).IsRequired();
    }
}
