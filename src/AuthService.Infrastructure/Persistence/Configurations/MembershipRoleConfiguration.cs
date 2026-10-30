using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class MembershipRoleConfiguration : IEntityTypeConfiguration<MembershipRole>
{
    public void Configure(EntityTypeBuilder<MembershipRole> builder)
    {
        builder.HasKey(assignment => assignment.Id);
        builder.Property(assignment => assignment.Id).ValueGeneratedNever();

        // Both foreign keys include TenantId, so the database rejects a role or membership from another tenant.
        builder.HasOne<TenantMembership>()
            .WithMany()
            .HasForeignKey(assignment => new { assignment.TenantId, assignment.MembershipId })
            .HasPrincipalKey(membership => new { membership.TenantId, membership.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(assignment => new { assignment.TenantId, assignment.RoleId })
            .HasPrincipalKey(role => new { role.TenantId, role.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(assignment => new { assignment.MembershipId, assignment.RoleId }).IsUnique();

        builder.Property(assignment => assignment.AssignedAt).IsRequired();
    }
}
