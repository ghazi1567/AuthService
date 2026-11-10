using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Entitlements;

/// <summary>
/// A tenant's entitlements as a product reads them (ENT-05): the same fields as the <c>EntitlementsChanged</c> event, plus the subscription's
/// status and its change number, the position to resume the "what changed since" read from.
/// </summary>
public sealed record TenantEntitlements(
    Guid TenantId,
    Guid ApplicationId,
    string Status,
    string? PlanKey,
    string? PlanName,
    long Version,
    long ChangeNumber,
    SortedDictionary<string, long?> Entitlements,
    IReadOnlyList<string> Overridden);

/// <summary>Where the next page of the product's entitlements starts: pass it back as <c>after</c>.</summary>
public sealed record ChangeCursor(long After);

/// <summary>Paging of <c>GET /api/auth/applications/me/entitlements</c>: <c>after</c> is a change number, <c>limit</c> at most 500.</summary>
public sealed record EntitlementFeedQuery(int? Limit, long? After)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public int Take => Limit ?? DefaultLimit;

    public IResult? Problem()
    {
        var errors = new Dictionary<string, string[]>();
        if (Limit is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"Limit must be between 1 and {MaxLimit}."];
        }

        if (After is < 0)
        {
            errors["after"] = ["After is a change number this list returned, never negative."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }
}

/// <summary>One limit as the tenant's own admins see it: what it is called and its value. Read-only, and without override reasons.</summary>
public sealed record TenantLimitItem(string Key, string Label, string? Description, string? Unit, long? Value, bool Overridden);

/// <summary>The tenant's own plan and limits for one product, for its admins.</summary>
public sealed record TenantOwnEntitlements(
    Guid ApplicationId,
    string ApplicationKey,
    string ApplicationName,
    string Status,
    string? PlanKey,
    string? PlanName,
    long Version,
    IReadOnlyList<TenantLimitItem> Limits);

/// <summary>
/// ENT-05: reading entitlements instead of waiting for an event. A product reads one tenant, or all its tenants in the order they changed (first
/// sync, and catching up after missed events); a tenant admin reads their own organisation's plan and limits. A product only ever sees its
/// own subscriptions (from the verified token) and a tenant token only its own tenant.
/// </summary>
public static class EntitlementReadEndpoints
{
    public const string ProductTenantRoute = "/api/auth/tenants/{tenantId:guid}/subscriptions/me/entitlements";
    public const string ProductFeedRoute = "/api/auth/applications/me/entitlements";
    public const string TenantRoute = "/api/auth/tenants/me/subscriptions/{applicationId:guid}/entitlements";

    public static string ProductTenantRouteFor(Guid tenantId) => $"/api/auth/tenants/{tenantId}/subscriptions/me/entitlements";

    public static string TenantRouteFor(Guid applicationId) => $"/api/auth/tenants/me/subscriptions/{applicationId}/entitlements";

    public static IEndpointRouteBuilder MapEntitlementReads(this IEndpointRouteBuilder app)
    {
        app.MapGet(ProductTenantRoute, ProductTenantAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        app.MapGet(ProductFeedRoute, ProductFeedAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        app.MapGet(TenantRoute, TenantAsync).RequireTenantPermission(PermissionKeys.SubscriptionsRead, AccessLevel.ReadOnly);
        return app;
    }

    /// <summary>One tenant's entitlements for the calling product, whatever the subscription's status. 404 if the tenant never subscribed.</summary>
    private static async Task<IResult> ProductTenantAsync(
        Guid tenantId, ClaimsPrincipal caller, AuthDbContext db, EntitlementState state, CancellationToken cancellationToken)
    {
        var applicationId = ApplicationId(caller);
        var subscription = await db.Subscriptions.AsNoTracking()
            .Where(candidate => candidate.TenantId == tenantId && candidate.ApplicationId == applicationId)
            .Select(candidate => new SubscriptionRow(candidate.Id, candidate.TenantId, candidate.Status, candidate.EntitlementsVersion, candidate.EntitlementsChangeSeq))
            .SingleOrDefaultAsync(cancellationToken);

        if (subscription is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "subscription_not_found", "Subscription not found", "This tenant is not subscribed to this product.");
        }

        return Results.Ok((await ToItemsAsync(state, applicationId, [subscription], cancellationToken))[0]);
    }

    /// <summary>
    /// Every subscription of the calling product, oldest change first. With no <c>after</c> it starts at the beginning (first sync); with the
    /// change number of the last item already read, it returns only the subscriptions whose entitlements changed since.
    /// </summary>
    private static async Task<IResult> ProductFeedAsync(
        [AsParameters] EntitlementFeedQuery query, ClaimsPrincipal caller, AuthDbContext db, EntitlementState state, CancellationToken cancellationToken)
    {
        if (query.Problem() is { } problem)
        {
            return problem;
        }

        var applicationId = ApplicationId(caller);
        var after = query.After ?? 0;
        var rows = await db.Subscriptions.AsNoTracking()
            .Where(candidate => candidate.ApplicationId == applicationId && candidate.EntitlementsChangeSeq > after)
            .OrderBy(candidate => candidate.EntitlementsChangeSeq)
            .Take(query.Take + 1)
            .Select(candidate => new SubscriptionRow(candidate.Id, candidate.TenantId, candidate.Status, candidate.EntitlementsVersion, candidate.EntitlementsChangeSeq))
            .ToListAsync(cancellationToken);

        var page = rows.Take(query.Take).ToList();
        var items = page.Count == 0 ? [] : await ToItemsAsync(state, applicationId, page, cancellationToken);
        var next = rows.Count > query.Take ? new ChangeCursor(page[^1].ChangeSeq) : null;
        return Results.Ok(new PagedResponse<TenantEntitlements, ChangeCursor>(items, next));
    }

    /// <summary>The caller's own tenant's plan and limits for one product. The built-in application and a product never subscribed are not found.</summary>
    private static async Task<IResult> TenantAsync(
        Guid applicationId, ClaimsPrincipal caller, AuthDbContext db, EntitlementState state, CancellationToken cancellationToken)
    {
        // The tenant is the token's `tid` and nothing else.
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var subscription = await (
                from candidate in db.Subscriptions.AsNoTracking()
                join application in db.Applications.AsNoTracking() on candidate.ApplicationId equals application.Id
                where candidate.TenantId == tenantId && candidate.ApplicationId == applicationId && !application.IsBuiltIn
                select new { candidate.Id, candidate.PlanId, candidate.Status, candidate.EntitlementsVersion, application.Key, application.Name })
            .SingleOrDefaultAsync(cancellationToken);

        if (subscription is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "subscription_not_found", "Subscription not found", "Your organisation is not subscribed to this product.");
        }

        var snapshot = (await state.SnapshotAsync(applicationId, [subscription.Id], cancellationToken))[subscription.Id];
        var plans = await state.PlanNamesAsync([subscription.PlanId], cancellationToken);
        var plan = subscription.PlanId is { } planId ? plans[planId] : default((string Key, string Name)?);
        var limits = await db.LimitDefinitions.AsNoTracking()
            .Where(limit => limit.ApplicationId == applicationId)
            .ToDictionaryAsync(limit => limit.Key, StringComparer.Ordinal, cancellationToken);

        return Results.Ok(new TenantOwnEntitlements(
            applicationId,
            subscription.Key,
            subscription.Name,
            subscription.Status.ToString(),
            plan?.Key,
            plan?.Name,
            subscription.EntitlementsVersion,
            snapshot.Limits.Select(pair => new TenantLimitItem(
                pair.Key, limits[pair.Key].Label, limits[pair.Key].Description, limits[pair.Key].Unit, pair.Value.Value,
                pair.Value.Source == EntitlementSource.Override)).ToList()));
    }

    /// <summary>The rows as the product reads them, in the order given, built exactly like the events' payload.</summary>
    private static async Task<List<TenantEntitlements>> ToItemsAsync(
        EntitlementState state, Guid applicationId, IReadOnlyList<SubscriptionRow> rows, CancellationToken cancellationToken)
    {
        var snapshots = await state.SnapshotAsync(applicationId, rows.Select(row => row.Id).ToList(), cancellationToken);
        var plans = await state.PlanNamesAsync(snapshots.Values.Select(snapshot => snapshot.PlanId), cancellationToken);
        return rows.Select(row =>
        {
            var snapshot = snapshots[row.Id];
            var plan = snapshot.PlanId is { } planId ? plans[planId] : default((string Key, string Name)?);
            var payload = EntitlementState.Payload(snapshot, row.Version, plan?.Key, plan?.Name);
            return new TenantEntitlements(
                row.TenantId, applicationId, row.Status.ToString(), payload.PlanKey, payload.PlanName, payload.Version, row.ChangeSeq,
                payload.Entitlements, payload.Overridden);
        }).ToList();
    }

    private static Guid ApplicationId(ClaimsPrincipal caller) =>
        caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");

    private sealed record SubscriptionRow(
        Guid Id, Guid TenantId, SubscriptionStatus Status, long Version, long ChangeSeq);
}
