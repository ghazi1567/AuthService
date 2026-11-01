using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class ApplicationConfiguration : IEntityTypeConfiguration<Application>
{
    /// <summary>Unique-index names, so callers can tell which rule a duplicate broke.</summary>
    public const string UniqueKeyIndexName = "ux_applications_key";

    public const string UniqueAudienceIndexName = "ux_applications_jwt_audience";

    public void Configure(EntityTypeBuilder<Application> builder)
    {
        builder.HasKey(application => application.Id);
        builder.Property(application => application.Id).ValueGeneratedNever();

        builder.Property(application => application.Key).HasMaxLength(63).IsRequired();
        builder.HasIndex(application => application.Key).IsUnique().HasDatabaseName(UniqueKeyIndexName);

        builder.Property(application => application.Name).HasMaxLength(200).IsRequired();

        builder.Property(application => application.JwtAudience).HasMaxLength(200).IsRequired();
        builder.HasIndex(application => application.JwtAudience).IsUnique().HasDatabaseName(UniqueAudienceIndexName);

        builder.Property(application => application.ClientId).HasMaxLength(64).IsRequired();
        builder.HasIndex(application => application.ClientId).IsUnique();

        builder.Property(application => application.IsActive).IsRequired();
        builder.Property(application => application.IsBuiltIn).IsRequired().HasDefaultValue(false);
        builder.Property(application => application.WebhookUrl).HasMaxLength(2000);
        builder.Property(application => application.WebhookSecretProtected).HasMaxLength(500);
        builder.Property(application => application.CreatedAt).IsRequired();
    }
}

public sealed class ApplicationSecretConfiguration : IEntityTypeConfiguration<ApplicationSecret>
{
    public void Configure(EntityTypeBuilder<ApplicationSecret> builder)
    {
        builder.HasKey(secret => secret.Id);
        builder.Property(secret => secret.Id).ValueGeneratedNever();

        builder.HasOne<Application>().WithMany().HasForeignKey(secret => secret.ApplicationId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(secret => secret.SecretHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(secret => secret.SecretHash).IsUnique();
        builder.HasIndex(secret => secret.ApplicationId);

        builder.Property(secret => secret.CreatedAt).IsRequired();
        builder.Property(secret => secret.ExpiresAt);
    }
}
