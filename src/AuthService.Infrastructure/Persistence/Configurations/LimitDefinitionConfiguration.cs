using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class LimitDefinitionConfiguration : IEntityTypeConfiguration<LimitDefinition>
{
    public const string UniqueKeyPerApplicationIndexName = "ux_limit_definitions_application_id_key";

    public void Configure(EntityTypeBuilder<LimitDefinition> builder)
    {
        builder.HasKey(limit => limit.Id);
        builder.Property(limit => limit.Id).ValueGeneratedNever();

        builder.HasOne<Application>().WithMany().HasForeignKey(limit => limit.ApplicationId).OnDelete(DeleteBehavior.Restrict);

        // Unique per product, not globally: two products may both limit "seats".
        builder.Property(limit => limit.Key).HasMaxLength(100).IsRequired();
        builder.HasIndex(limit => new { limit.ApplicationId, limit.Key })
            .IsUnique()
            .HasDatabaseName(UniqueKeyPerApplicationIndexName);

        builder.Property(limit => limit.Label).HasMaxLength(200).IsRequired();
        builder.Property(limit => limit.Description).HasMaxLength(1000);
        builder.Property(limit => limit.Unit).HasMaxLength(50);
        builder.ToTable(table => table.HasCheckConstraint("ck_limit_definitions_default_value_not_negative", "default_value IS NULL OR default_value >= 0"));
        builder.Property(limit => limit.CreatedAt).IsRequired();
        builder.Property(limit => limit.UpdatedAt).IsRequired();
    }
}
