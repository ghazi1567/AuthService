using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.HasKey(key => key.Id);
        builder.Property(key => key.Id).ValueGeneratedNever();

        // (TenantId, Id) is a second key so ApiKeyPermission can reference both and prove its tenant matches.
        builder.HasAlternateKey(key => new { key.TenantId, key.Id });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(key => key.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Application>().WithMany().HasForeignKey(key => key.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(key => key.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(key => key.Name).HasMaxLength(100).IsRequired();

        // The key is looked up by its public part.
        builder.Property(key => key.KeyId).HasMaxLength(24).IsRequired();
        builder.HasIndex(key => key.KeyId).IsUnique();

        builder.Property(key => key.SecretSalt).HasMaxLength(64).IsRequired();
        builder.Property(key => key.SecretHash).HasMaxLength(64).IsRequired();

        builder.HasIndex(key => key.TenantId);

        builder.Property(key => key.CreatedAt).IsRequired();
        builder.Property(key => key.ExpiresAt);
        builder.Property(key => key.RevokedAt);
    }
}

public sealed class ApiKeyPermissionConfiguration : IEntityTypeConfiguration<ApiKeyPermission>
{
    public void Configure(EntityTypeBuilder<ApiKeyPermission> builder)
    {
        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.Id).ValueGeneratedNever();

        // The foreign key includes TenantId, so the database rejects a key from another tenant.
        builder.HasOne<ApiKey>()
            .WithMany()
            .HasForeignKey(grant => new { grant.TenantId, grant.ApiKeyId })
            .HasPrincipalKey(key => new { key.TenantId, key.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Permission>().WithMany().HasForeignKey(grant => grant.PermissionId).OnDelete(DeleteBehavior.Restrict);

        // A key holds a permission at most once.
        builder.HasIndex(grant => new { grant.ApiKeyId, grant.PermissionId }).IsUnique();

        builder.Property(grant => grant.Level).HasConversion<string>().HasMaxLength(20).IsRequired();
    }
}
