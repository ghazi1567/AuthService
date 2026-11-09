using AuthService.Api.Common;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Applications;

/// <param name="PermissionKeys">The keys of the product's permission catalogue, in order (ONB-03), so a product can check that the keys its code
/// asks for are all registered.</param>
public sealed record CurrentApplicationResponse(
    Guid Id,
    string Key,
    string Name,
    string JwtAudience,
    string ClientId,
    bool IsActive,
    IReadOnlyList<string> PermissionKeys);

/// <summary>
/// What a product can ask about itself with its service token. Which product it is comes only from the verified token, never
/// from the request, so a product can see only its own record. The later product-side endpoints (subscriptions, seat limits, API
/// keys) are scoped the same way.
/// </summary>
public static class CurrentApplicationEndpoints
{
    public const string Route = "/api/auth/applications/me";

    public static IEndpointRouteBuilder MapCurrentApplication(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, GetAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        return app;
    }

    private static async Task<IResult> GetAsync(System.Security.Claims.ClaimsPrincipal caller, AuthDbContext db, CancellationToken cancellationToken)
    {
        var applicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");

        var application = await db.Applications.AsNoTracking()
            .Where(candidate => candidate.Id == applicationId)
            .Select(candidate => new { candidate.Id, candidate.Key, candidate.Name, candidate.JwtAudience, candidate.ClientId, candidate.IsActive })
            .SingleOrDefaultAsync(cancellationToken);
        if (application is null)
        {
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized", "A valid access token is required.");
        }

        var keys = await db.Permissions.AsNoTracking()
            .Where(permission => permission.ApplicationId == applicationId)
            .Select(permission => permission.Key)
            .ToListAsync(cancellationToken);
        keys.Sort(StringComparer.Ordinal);

        return Results.Ok(new CurrentApplicationResponse(
            application.Id, application.Key, application.Name, application.JwtAudience, application.ClientId, application.IsActive, keys));
    }
}
