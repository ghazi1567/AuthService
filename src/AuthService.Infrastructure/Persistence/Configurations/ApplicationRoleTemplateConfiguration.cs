using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class ApplicationRoleTemplateConfiguration : IEntityTypeConfiguration<ApplicationRoleTemplate>
{
    public const string UniqueNamePerApplicationIndexName = "ux_application_role_templates_application_id_name";

    public void Configure(EntityTypeBuilder<ApplicationRoleTemplate> builder)
    {
        builder.ToTable("application_role_templates");
        builder.HasKey(template => template.Id);
        builder.Property(template => template.Id).ValueGeneratedNever();

        builder.HasOne<Application>().WithMany().HasForeignKey(template => template.ApplicationId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(template => template.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(template => new { template.ApplicationId, template.Name })
            .IsUnique()
            .HasDatabaseName(UniqueNamePerApplicationIndexName);

        builder.Property(template => template.Description).HasMaxLength(500);
        builder.Property(template => template.CreatedAt).IsRequired();
        builder.Property(template => template.UpdatedAt).IsRequired();
    }
}

public sealed class ApplicationRoleTemplatePermissionConfiguration : IEntityTypeConfiguration<ApplicationRoleTemplatePermission>
{
    public void Configure(EntityTypeBuilder<ApplicationRoleTemplatePermission> builder)
    {
        builder.ToTable("application_role_template_permissions");
        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.Id).ValueGeneratedNever();

        builder.HasOne<ApplicationRoleTemplate>().WithMany().HasForeignKey(grant => grant.TemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Permission>().WithMany().HasForeignKey(grant => grant.PermissionId).OnDelete(DeleteBehavior.Restrict);

        // A template grants a permission at most once, at one level.
        builder.HasIndex(grant => new { grant.TemplateId, grant.PermissionId }).IsUnique();
        builder.HasIndex(grant => grant.PermissionId);

        builder.Property(grant => grant.Level).HasConversion<string>().HasMaxLength(20).IsRequired();
    }
}
