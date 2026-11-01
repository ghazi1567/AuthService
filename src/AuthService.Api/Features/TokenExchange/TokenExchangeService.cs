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
    /// no active membership, no active subscription, no access grant, an inactive product or a suspended tenant.
    /// </summary>
    public sealed record Denied : TokenExchangeResult;
}

/// <summary>
/// TOK-01: trades a person's identity session for a token scoped to one tenant and one product. The caller must have an Active
/// membership in the tenant, the tenant an Active subscription to the product, and the membership an access grant (a seat)
/// for it. Everything is read fresh from the database, so a suspension or revoked access applies at the very next exchange.
/// </summary>
public sealed class TokenExchangeService(
    AuthDbContext db,
    IdentityTokenService tokens,
    IOptions<IdentityTokenOptions> tokenOptions,
    TimeProvider timeProvider)
{
    public async Task<TokenExchangeResult> ExchangeAsync(Guid userId, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        // Only the caller's own membership counts: Invited, Disabled and none at all are all refused.
        var membershipId = await (
            from membership in db.TenantMemberships
            join tenant in db.Tenants on membership.TenantId equals tenant.Id
            where membership.TenantId == tenantId && membership.UserId == userId
                && membership.Status == MembershipStatus.Active
                && tenant.Status != TenantStatus.Suspended
            select (Guid?)membership.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (membershipId is null)
        {
            return new TokenExchangeResult.Denied();
        }

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

        var permissions = await ResolvePermissionsAsync(tenantId, membershipId.Value, applicationId, cancellationToken);
        var token = tokens.CreateProductToken(
            userId, tenantId, application.JwtAudience,
            [.. permissions.Where(pair => pair.Value > AccessLevel.None).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{pair.Value}")],
            timeProvider.GetUtcNow());

        return new TokenExchangeResult.Issued(new TokenExchangeResponse(token.Value, "Bearer", tokenOptions.Value.ProductTokenMinutes * 60));
    }

    /// <summary>
    /// The highest level the member holds for each permission of this one product. The built-in Owner role holds Full on the
    /// product's whole catalog as it is right now, with no grants stored, so a permission added later is covered at once.
    /// Grants from custom roles (ROL-01) are merged in, highest level per permission.
    /// </summary>
    private async Task<Dictionary<string, AccessLevel>> ResolvePermissionsAsync(
        Guid tenantId, Guid membershipId, Guid applicationId, CancellationToken cancellationToken)
    {
        var effective = new Dictionary<string, AccessLevel>(StringComparer.Ordinal);

        var isOwner = await (
            from assignment in db.MembershipRoles
            join role in db.Roles on new { assignment.TenantId, assignment.RoleId } equals new { role.TenantId, RoleId = role.Id }
            where assignment.TenantId == tenantId && assignment.MembershipId == membershipId
                && role.IsBuiltIn && role.Name == Role.OwnerName
            select assignment.Id)
            .AnyAsync(cancellationToken);

        if (isOwner)
        {
            var catalog = await db.Permissions.AsNoTracking()
                .Where(permission => permission.ApplicationId == applicationId)
                .Select(permission => permission.Key)
                .ToListAsync(cancellationToken);
            foreach (var key in catalog)
            {
                effective[key] = AccessLevel.Full;
            }
        }

        // Grants from every role the member holds, for this product only. Where several roles grant the same permission, the
        // highest level wins; there are no deny rules, so another role can never take access away.
        var grants = await (
            from assignment in db.MembershipRoles
            join grant in db.RolePermissions on new { assignment.TenantId, assignment.RoleId } equals new { grant.TenantId, grant.RoleId }
            join permission in db.Permissions on grant.PermissionId equals permission.Id
            where assignment.TenantId == tenantId && assignment.MembershipId == membershipId && permission.ApplicationId == applicationId
            select new { permission.Key, grant.Level })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var grant in grants)
        {
            if (!effective.TryGetValue(grant.Key, out var current) || grant.Level > current)
            {
                effective[grant.Key] = grant.Level;
            }
        }

        return effective;
    }
}
