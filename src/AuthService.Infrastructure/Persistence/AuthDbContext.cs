using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the Auth Service database (PostgreSQL).
/// Entity sets are added by the feature stories (TEN, APP, SUB, ROL, TOK, KEY), each with its own migration.
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<MembershipRole> MembershipRoles => Set<MembershipRole>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // citext backs case-insensitive columns such as User.Email (see TEN-01 / AUTH-8).
        // Declared in the model so the migrations and the model stay in sync (TST_TC2).
        modelBuilder.HasPostgresExtension("citext");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
