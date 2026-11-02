using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.TokenExchange;

/// <summary>Body of <c>POST /api/auth/token/exchange</c>. Nullable so a missing field is reported as a validation error.</summary>
public sealed record TokenExchangeRequest(Guid? TenantId, Guid? ApplicationId);

public sealed record TokenExchangeResponse(string AccessToken, string TokenType, int ExpiresIn);

public abstract record TokenExchangeResult
{
    public sealed record Issued(TokenExchangeResponse Response) : TokenExchangeResult;

    /// <summary>
    /// One answer for every reason, so a caller learns nothing about tenants, memberships or subscriptions they have no part in:
    /// no active membership, no active subscription, no access grant or an inactive product.
    /// </summary>
    public sealed record Denied : TokenExchangeResult;

    /// <summary>
    /// The one reason that is told apart (TOK-05): the caller is an Active member of a tenant that is suspended. They already know the
    /// tenant exists and that they belong to it, so saying it is suspended reveals nothing new, and it lets them understand why nothing opens.
    /// Anyone else, including someone whose membership is not Active, still gets <see cref="Denied"/>.
    /// </summary>
    public sealed record TenantSuspended : TokenExchangeResult;
}

/// <summary>
/// TOK-01: trades a person's identity session for a token scoped to one tenant and one product. The caller must have an Active
/// membership in the tenant, the tenant an Active subscription to the product, and the membership an access grant (a seat)
/// for it. Everything is read fresh from the database, so a suspension or revoked access applies at the very next exchange.
/// </summary>
public sealed class TokenExchangeService(
    AuthDbContext db,
    PermissionResolver resolver,
    IdentityTokenService tokens,
    IOptions<IdentityTokenOptions> tokenOptions,
    TimeProvider timeProvider)
{
    public async Task<TokenExchangeResult> ExchangeAsync(Guid userId, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        // Only the caller's own membership counts: Invited, Disabled and none at all are all refused.
        var standing = await (
            from membership in db.TenantMemberships
            join tenant in db.Tenants on membership.TenantId equals tenant.Id
            where membership.TenantId == tenantId && membership.UserId == userId
                && membership.Status == MembershipStatus.Active
            select new { MembershipId = membership.Id, Suspended = tenant.Status == TenantStatus.Suspended })
            .SingleOrDefaultAsync(cancellationToken);
        if (standing is null)
        {
            return new TokenExchangeResult.Denied();
        }

        if (standing.Suspended)
        {
            return new TokenExchangeResult.TenantSuspended();
        }

        Guid? membershipId = standing.MembershipId;

        var application = await db.Applications.AsNoTracking()
            .Where(candidate => candidate.Id == applicationId && candidate.IsActive)
            .Select(candidate => new { candidate.JwtAudience, candidate.IsBuiltIn })
            .SingleOrDefaultAsync(cancellationToken);
        if (application is null)
        {
            return new TokenExchangeResult.Denied();
        }

        // The Auth Service's own product needs neither a subscription nor a seat: every Active member of a tenant may work in it,
        // and what they may do there is decided by their roles alone.
        if (!application.IsBuiltIn)
        {
            var subscribed = await db.Subscriptions.AnyAsync(
                subscription => subscription.TenantId == tenantId && subscription.ApplicationId == applicationId
                    && subscription.Status == SubscriptionStatus.Active, cancellationToken);
            if (!subscribed)
            {
                return new TokenExchangeResult.Denied();
            }

            // Having roles that grant permissions is not enough: the member must also have been given access to the product.
            var hasAccess = await db.ApplicationAccess.AnyAsync(
                access => access.TenantId == tenantId && access.MembershipId == membershipId && access.ApplicationId == applicationId,
                cancellationToken);
            if (!hasAccess)
            {
                return new TokenExchangeResult.Denied();
            }
        }

        var permissions = await resolver.ResolveAsync(tenantId, membershipId.Value, applicationId, cancellationToken);
        var token = tokens.CreateProductToken(
            userId, tenantId, application.JwtAudience,
            [.. permissions.Where(pair => pair.Value > AccessLevel.None).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{pair.Value}")],
            timeProvider.GetUtcNow());

        return new TokenExchangeResult.Issued(new TokenExchangeResponse(token.Value, "Bearer", tokenOptions.Value.ProductTokenMinutes * 60));
    }
}
