using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Login;

public abstract record LoginResult
{
    public sealed record Succeeded(LoginResponse Response) : LoginResult;

    /// <summary>Wrong password, unknown email, or an account with no password yet. Deliberately not told apart.</summary>
    public sealed record InvalidCredentials : LoginResult;

    public sealed record AccountDisabled : LoginResult;

    public sealed record EmailNotVerified : LoginResult;
}

/// <summary>TEN-02: checks a person's email and password and issues an identity access token and a refresh token.</summary>
public sealed class LoginService(
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher,
    IdentityTokenService tokens,
    TimeProvider timeProvider)
{
    // Hashed once so an unknown email still costs one password check: response time must not reveal whether an email exists.
    private static readonly User DummyUser = new() { Email = "dummy@invalid" };
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser, "not-a-real-password");

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Email == email, cancellationToken);

        if (user?.PasswordHash is null)
        {
            passwordHasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return new LoginResult.InvalidCredentials();
        }

        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            return new LoginResult.InvalidCredentials();
        }

        // Past this point the password was right, so telling the person why they cannot log in leaks nothing to a guesser.
        var memberships = await (
                from membership in db.TenantMemberships
                join tenant in db.Tenants on membership.TenantId equals tenant.Id
                where membership.UserId == user.Id
                orderby tenant.Name, tenant.Id
                select new MembershipSummary(tenant.Id, tenant.Name, membership.Status))
            .ToListAsync(cancellationToken);

        // "Disabled" is a property of memberships (TEN-07). A person with no usable membership left cannot log in.
        if (memberships.Count > 0 && memberships.All(membership => membership.Status == MembershipStatus.Disabled))
        {
            return new LoginResult.AccountDisabled();
        }

        if (user.EmailVerifiedAt is null)
        {
            return new LoginResult.EmailNotVerified();
        }

        var now = timeProvider.GetUtcNow();
        var accessToken = tokens.CreateAccessToken(user.Id, now);
        var refreshToken = tokens.CreateRefreshToken(user.Id, now);

        db.RefreshTokens.Add(refreshToken.Entity);
        await db.SaveChangesAsync(cancellationToken);

        return new LoginResult.Succeeded(new LoginResponse(
            accessToken.Value,
            "Bearer",
            (int)(accessToken.ExpiresAt - now).TotalSeconds,
            refreshToken.Value,
            memberships));
    }
}
