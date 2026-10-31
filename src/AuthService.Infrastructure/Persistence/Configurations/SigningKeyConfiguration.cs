using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class SigningKeyConfiguration : IEntityTypeConfiguration<SigningKey>
{
    public void Configure(EntityTypeBuilder<SigningKey> builder)
    {
        builder.HasKey(key => key.Id);
        builder.Property(key => key.Id).ValueGeneratedNever();

        builder.Property(key => key.KeyId).HasMaxLength(64).IsRequired();
        builder.HasIndex(key => key.KeyId).IsUnique();

        builder.Property(key => key.PublicKeyX).HasMaxLength(64).IsRequired();
        builder.Property(key => key.PublicKeyY).HasMaxLength(64).IsRequired();
        builder.Property(key => key.EncryptedPrivateKey).IsRequired();

        builder.Property(key => key.CreatedAt).IsRequired();
        builder.Property(key => key.PublishFrom).IsRequired();
        builder.Property(key => key.SignFrom).IsRequired();
        builder.Property(key => key.SignUntil);
        builder.Property(key => key.PublishUntil);
    }
}
