using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.HasKey(invitation => invitation.Id);
        builder.Property(invitation => invitation.Id).ValueGeneratedNever();

        // The foreign key includes TenantId, so the database rejects a membership from another tenant.
        builder.HasOne<TenantMembership>()
            .WithMany()
            .HasForeignKey(invitation => new { invitation.TenantId, invitation.MembershipId })
            .HasPrincipalKey(membership => new { membership.TenantId, membership.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>().WithMany().HasForeignKey(invitation => invitation.InvitedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(invitation => invitation.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(invitation => invitation.TokenHash).IsUnique();
        builder.HasIndex(invitation => invitation.MembershipId);

        builder.Property(invitation => invitation.CreatedAt).IsRequired();
        builder.Property(invitation => invitation.LastSentAt).IsRequired();
        builder.Property(invitation => invitation.ExpiresAt).IsRequired();
    }
}
