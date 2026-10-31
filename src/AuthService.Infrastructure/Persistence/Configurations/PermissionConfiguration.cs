using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public const string UniqueKeyPerApplicationIndexName = "ux_permissions_application_id_key";

    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.HasKey(permission => permission.Id);
        builder.Property(permission => permission.Id).ValueGeneratedNever();

        builder.HasOne<Application>().WithMany().HasForeignKey(permission => permission.ApplicationId).OnDelete(DeleteBehavior.Restrict);

        // Unique per product, not globally: two products may both have "report.export".
        builder.Property(permission => permission.Key).HasMaxLength(100).IsRequired();
        builder.HasIndex(permission => new { permission.ApplicationId, permission.Key })
            .IsUnique()
            .HasDatabaseName(UniqueKeyPerApplicationIndexName);

        builder.Property(permission => permission.Label).HasMaxLength(200).IsRequired();
        builder.Property(permission => permission.Description).HasMaxLength(1000);
        builder.Property(permission => permission.CreatedAt).IsRequired();
        builder.Property(permission => permission.UpdatedAt).IsRequired();
    }
}
