using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.Id).ValueGeneratedNever();

        // The foreign key includes TenantId, so the database rejects a role from another tenant.
        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(grant => new { grant.TenantId, grant.RoleId })
            .HasPrincipalKey(role => new { role.TenantId, role.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Permission>().WithMany().HasForeignKey(grant => grant.PermissionId).OnDelete(DeleteBehavior.Restrict);

        // A role grants a permission at most once, at one level.
        builder.HasIndex(grant => new { grant.RoleId, grant.PermissionId }).IsUnique();

        // Token exchange looks grants up by permission.
        builder.HasIndex(grant => grant.PermissionId);

        builder.Property(grant => grant.Level).HasConversion<string>().HasMaxLength(20).IsRequired();
    }
}
