using AuthService.Api.Features.Accounts;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Login;

public abstract record LoginResult
{
    public sealed record Succeeded(LoginResponse Response) : LoginResult;

    /// <summary>
    /// Wrong password, unknown email, an account with no password yet, or a locked account. Deliberately not told apart.
    /// </summary>
    public sealed record InvalidCredentials : LoginResult;

    public sealed record AccountDisabled : LoginResult;

    public sealed record EmailNotVerified : LoginResult;
}

/// <summary>
/// TEN-02: checks a person's email and password and issues an identity access token and a refresh token.
/// TEN-12: counts wrong passwords and locks the account after too many in a row.
/// </summary>
public sealed class LoginService(
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher,
    IdentityTokenService tokens,
    IOptions<LoginProtectionOptions> options,
    TimeProvider timeProvider,
    ILogger<LoginService> logger)
{
    // Hashed once so an unknown email still costs one password check: response time must not reveal whether an email exists.
    private static readonly User DummyUser = new() { Email = "dummy@invalid" };
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser, "not-a-real-password");

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Email == email, cancellationToken);

        if (user?.PasswordHash is null)
        {
            passwordHasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return new LoginResult.InvalidCredentials();
        }

        // A locked account is refused whatever password is entered, even the right one, with the same answer as a wrong
        // password. The check below still costs one password hash, so how long the answer takes does not give the lock away.
        // Attempts during a lock are not counted, so they cannot extend it.
        if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            passwordHasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return new LoginResult.InvalidCredentials();
        }

        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            await RegisterFailureAsync(user.Id, now, cancellationToken);
            return new LoginResult.InvalidCredentials();
        }

        // A right password ends the run of failures (and any lock that has run out).
        if (user.FailedLoginCount > 0 || user.LockedUntil is not null)
        {
            await db.Users
                .Where(candidate => candidate.Id == user.Id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.FailedLoginCount, 0)
                        .SetProperty(candidate => candidate.LockedUntil, (DateTimeOffset?)null),
                    cancellationToken);
        }

        // Past this point the password was right, so telling the person why they cannot log in leaks nothing to a guesser.
        var memberships = await db.ListMembershipsAsync(user.Id, cancellationToken);

        if (AccountAccess.IsDisabled(memberships.ConvertAll(membership => membership.Status)))
        {
            return new LoginResult.AccountDisabled();
        }

        if (user.EmailVerifiedAt is null)
        {
            return new LoginResult.EmailNotVerified();
        }

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

    /// <summary>
    /// Counts one wrong password and, when it is the last one allowed, locks the account and starts the count again. One
    /// UPDATE does both, so simultaneous wrong passwords cannot be miscounted or slip past the limit.
    /// </summary>
    private async Task RegisterFailureAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var max = settings.MaxFailedAttempts;
        var lockUntil = now.AddMinutes(settings.LockoutMinutes);

        await db.Users
            .Where(candidate => candidate.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.FailedLoginCount, candidate => candidate.FailedLoginCount + 1 >= max ? 0 : candidate.FailedLoginCount + 1)
                    .SetProperty(candidate => candidate.LockedUntil, candidate => candidate.FailedLoginCount + 1 >= max ? lockUntil : candidate.LockedUntil),
                cancellationToken);

        var lockedUntil = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => candidate.LockedUntil)
            .SingleAsync(cancellationToken);

        // Postgres stores microseconds and .NET keeps finer ticks, so compare with a tolerance, not for equality.
        if (lockedUntil is { } storedLockedUntil && (storedLockedUntil - lockUntil).Duration() < TimeSpan.FromMilliseconds(1))
        {
            // Logged by user id, never by email. The audit log (AUD-01) will record this event too once it exists.
            logger.LogWarning(
                "Account {UserId} locked for {LockoutMinutes} minutes after {MaxFailedAttempts} failed logins.",
                userId, settings.LockoutMinutes, max);
        }
    }
}
