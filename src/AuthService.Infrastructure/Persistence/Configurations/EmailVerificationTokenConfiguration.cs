using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class EmailVerificationTokenConfiguration : IEntityTypeConfiguration<EmailVerificationToken>
{
    public void Configure(EntityTypeBuilder<EmailVerificationToken> builder)
    {
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();

        builder.HasOne<User>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(token => token.TokenHash).IsUnique();

        // Looked up by user to apply the resend cooldown.
        builder.HasIndex(token => new { token.UserId, token.CreatedAt });

        builder.Property(token => token.CreatedAt).IsRequired();
        builder.Property(token => token.ExpiresAt).IsRequired();
    }
}
