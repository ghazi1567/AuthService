using AuthService.Api.Features.EmailVerification;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Configurations;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Api.Features.Signup;

public abstract record SignupResult
{
    public sealed record Created(SignupResponse Response) : SignupResult;

    public sealed record EmailAlreadyRegistered : SignupResult;
}

/// <summary>
/// TEN-01: creates a tenant, a global user and that user's Active membership holding the built-in Owner role, and queues
/// the email that asks them to verify their address (TEN-11).
/// Everything is added to the context and saved with one <c>SaveChangesAsync</c>, which EF runs in a single
/// database transaction, so a failure part-way leaves no tenant, user, role, membership, token or queued email behind.
/// </summary>
public sealed class SignupService(
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher,
    EmailVerificationService emailVerification,
    TimeProvider timeProvider)
{
    public async Task<SignupResult> SignUpAsync(ValidSignup signup, CancellationToken cancellationToken)
    {
        // Friendly fast path. The unique index below is what actually guarantees it (two simultaneous signups).
        if (await db.Users.AnyAsync(user => user.Email == signup.Email, cancellationToken))
        {
            return new SignupResult.EmailAlreadyRegistered();
        }

        var now = timeProvider.GetUtcNow();

        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Name = signup.TenantName,
            Status = TenantStatus.Active,
            CreatedAt = now,
        };

        // EmailVerifiedAt stays null: the new user starts unverified and cannot log in until they open the emailed link.
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = signup.Email,
            DisplayName = signup.DisplayName,
            CreatedAt = now,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, signup.Password);

        var membership = new TenantMembership
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            UserId = user.Id,
            Status = MembershipStatus.Active,
            CreatedAt = now,
        };

        var ownerRole = new Role
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            Name = Role.OwnerName,
            IsBuiltIn = true,
            CreatedAt = now,
        };

        var ownerAssignment = new MembershipRole
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            MembershipId = membership.Id,
            RoleId = ownerRole.Id,
            AssignedAt = now,
        };

        db.AddRange(tenant, user, membership, ownerRole, ownerAssignment);

        // Queued in the same save, so the email exists only if the account does.
        emailVerification.Issue(user.Id, user.Email, now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsEmailConflict(exception))
        {
            return new SignupResult.EmailAlreadyRegistered();
        }

        return new SignupResult.Created(new SignupResponse(tenant.Id, user.Id, membership.Id, user.Email));
    }

    private static bool IsEmailConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: UserConfiguration.UniqueEmailIndexName,
        };
}
