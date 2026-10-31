using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <summary>Name of the unique index on email; callers use it to tell an email conflict from any other failure.</summary>
    public const string UniqueEmailIndexName = "ux_users_email";

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();

        // citext makes the unique index case-insensitive in the database, on top of the code lowercasing emails.
        builder.Property(user => user.Email).HasColumnType("citext").HasMaxLength(254).IsRequired();
        builder.HasIndex(user => user.Email).IsUnique().HasDatabaseName(UniqueEmailIndexName);

        builder.Property(user => user.PasswordHash).HasMaxLength(512);
        builder.Property(user => user.DisplayName).HasMaxLength(200);
        builder.Property(user => user.EmailVerifiedAt);
        builder.Property(user => user.FailedLoginCount).IsRequired();
        builder.Property(user => user.LockedUntil);
        builder.Property(user => user.CreatedAt).IsRequired();
    }
}
