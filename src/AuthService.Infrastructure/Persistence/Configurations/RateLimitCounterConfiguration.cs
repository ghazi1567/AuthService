using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Persistence.Configurations;

public sealed class RateLimitCounterConfiguration : IEntityTypeConfiguration<RateLimitCounter>
{
    public void Configure(EntityTypeBuilder<RateLimitCounter> builder)
    {
        builder.HasKey(counter => counter.KeyHash);
        builder.Property(counter => counter.KeyHash).HasMaxLength(64).ValueGeneratedNever();
        builder.Property(counter => counter.Hits).IsRequired();
        builder.Property(counter => counter.ExpiresAt).IsRequired();

        // The cleanup deletes by expiry.
        builder.HasIndex(counter => counter.ExpiresAt);
    }
}
