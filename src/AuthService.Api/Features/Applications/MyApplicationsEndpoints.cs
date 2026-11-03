using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Applications;

/// <summary>Where a product stands for one person in one organisation (APP-09).</summary>
public static class MyApplicationState
{
    /// <summary>Subscribed, active, and this person has a seat: it can be opened.</summary>
    public const string Available = "Available";

    /// <summary>Subscribed and active, but this person has not been given access.</summary>
    public const string NoAccess = "NoAccess";

    /// <summary>The organisation has no subscription to it.</summary>
    public const string NotSubscribed = "NotSubscribed";

    public const string SubscriptionSuspended = "SubscriptionSuspended";

    public const string SubscriptionCancelled = "SubscriptionCancelled";
}

/// <summary>
/// One available product as one person sees it in one organisation. <see cref="LaunchUrl"/> is only given for a product that can be
/// opened, so the picker can't be used to learn where a product that isn't theirs lives.
/// </summary>
public sealed record MyApplicationItem(Guid ApplicationId, string Key, string Name, string State, string? LaunchUrl);

/// <summary>APP-09: the signed-in person lists the products they can open in one organisation, and why the others can't be.</summary>
public static class MyApplicationsEndpoints
{
    public const string Route = "/api/auth/users/me/applications";

    public static IEndpointRouteBuilder MapMyApplications(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, ListAsync).RequireAuthorization(IdentityAuthentication.PolicyName);
        return app;
    }

    // Who is asking comes only from the verified token. The organisation is named in the query, and is checked against that person's own
    // active membership, so naming someone else's organisation shows nothing.
    private static async Task<IResult> ListAsync(
        Guid? tenantId,
        ClaimsPrincipal caller,
        AuthDbContext db,
        HttpContext httpContext,
        [AsParameters] PageQuery page,
        CancellationToken cancellationToken)
    {
        if (page.Problem(byName: true) is { } problem)
        {
            return problem;
        }

        if (caller.GetUserId() is not { } userId)
        {
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized", "A valid access token is required.");
        }

        if (tenantId is not { } tenant)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(tenantId)] = ["tenantId is required."] });
        }

        // The same rules as the token exchange: an active membership, in an organisation that is not suspended. An active member is told
        // when it is suspended (they already know it is theirs); anyone else is just refused.
        var standing = await (
            from membership in db.TenantMemberships
            join organisation in db.Tenants on membership.TenantId equals organisation.Id
            where membership.TenantId == tenant && membership.UserId == userId
                && membership.Status == MembershipStatus.Active
            select new { MembershipId = membership.Id, Suspended = organisation.Status == TenantStatus.Suspended })
            .SingleOrDefaultAsync(cancellationToken);
        if (standing is null)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "tenant_access_denied", "No access to this organisation",
                "You are not an active member of this organisation.");
        }

        if (standing.Suspended)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "tenant_suspended", "Organisation suspended",
                "This organisation is suspended, so nothing can be opened in it right now.");
        }

        Guid? membershipId = standing.MembershipId;

        var rows = await page.ByNameAsync(
            from application in db.Applications.AsNoTracking()
            where application.IsActive && !application.IsBuiltIn
            select new
            {
                application.Id,
                application.Key,
                application.Name,
                application.LaunchUrl,
                Subscription = db.Subscriptions
                    .Where(subscription => subscription.TenantId == tenant && subscription.ApplicationId == application.Id)
                    .Select(subscription => (SubscriptionStatus?)subscription.Status)
                    .FirstOrDefault(),
                HasSeat = db.ApplicationAccess.Any(access =>
                    access.TenantId == tenant && access.MembershipId == membershipId && access.ApplicationId == application.Id),
            },
            row => row.Name,
            row => row.Id,
            cancellationToken);

        var items = rows.Rows.Select(row =>
        {
            var state = row.Subscription switch
            {
                null => MyApplicationState.NotSubscribed,
                SubscriptionStatus.Suspended => MyApplicationState.SubscriptionSuspended,
                SubscriptionStatus.Cancelled => MyApplicationState.SubscriptionCancelled,
                _ => row.HasSeat ? MyApplicationState.Available : MyApplicationState.NoAccess,
            };
            return new MyApplicationItem(row.Id, row.Key, row.Name, state, state == MyApplicationState.Available ? row.LaunchUrl : null);
        });

        // Personal to this person and organisation: never to be stored by a shared cache.
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new PagedResponse<MyApplicationItem, NameCursor>([.. items], rows.Next));
    }
}
