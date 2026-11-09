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

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<ApiKeyPermission> ApiKeyPermissions => Set<ApiKeyPermission>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();

    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();

    /// <summary>The shared rate-limit counters (PLT-04). Written with raw SQL, an atomic upsert; here so the migration creates the table.</summary>
    public DbSet<RateLimitCounter> RateLimitCounters => Set<RateLimitCounter>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<Application> Applications => Set<Application>();

    public DbSet<ApplicationSecret> ApplicationSecrets => Set<ApplicationSecret>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<ApplicationRoleTemplate> RoleTemplates => Set<ApplicationRoleTemplate>();

    public DbSet<ApplicationRoleTemplatePermission> RoleTemplatePermissions => Set<ApplicationRoleTemplatePermission>();

    public DbSet<TenantApplicationSubscription> Subscriptions => Set<TenantApplicationSubscription>();

    public DbSet<MembershipApplicationAccess> ApplicationAccess => Set<MembershipApplicationAccess>();

    public DbSet<WebhookOutboxMessage> WebhookOutbox => Set<WebhookOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // citext backs case-insensitive columns such as User.Email (see TEN-01 / AUTH-8).
        // Declared in the model so the migrations and the model stay in sync (TST_TC2).
        modelBuilder.HasPostgresExtension("citext");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
