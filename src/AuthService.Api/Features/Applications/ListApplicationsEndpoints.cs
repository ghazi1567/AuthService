using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Applications;

/// <summary>
/// A product as a tenant sees it when choosing what to connect. Only what a tenant needs: the product's audience and client
/// credentials are for the product and the platform, not for tenants.
/// </summary>
public sealed record ApplicationSummary(Guid Id, string Key, string Name);

public static class ListApplicationsEndpoints
{
    public const string Route = "/api/auth/applications";

    public static IEndpointRouteBuilder MapListApplications(this IEndpointRouteBuilder app)
    {
        // Any signed-in person may look: it powers the "connect a new product" screen, so it needs no subscription and no tenant.
        app.MapGet(Route, ListAsync).RequireAuthorization(IdentityAuthentication.PolicyName);
        return app;
    }

    private static async Task<IResult> ListAsync(AuthDbContext db, CancellationToken cancellationToken)
    {
        // A product that is not active (no catalog yet, or withdrawn) has nothing to subscribe to, so it is not offered.
        var applications = await db.Applications.AsNoTracking()
            .Where(application => application.IsActive)
            .OrderBy(application => application.Name).ThenBy(application => application.Key)
            .Select(application => new ApplicationSummary(application.Id, application.Key, application.Name))
            .ToListAsync(cancellationToken);

        return Results.Ok(applications);
    }
}
