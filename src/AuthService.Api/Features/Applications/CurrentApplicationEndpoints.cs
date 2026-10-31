using AuthService.Api.Common;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Applications;

public sealed record CurrentApplicationResponse(Guid Id, string Key, string Name, string JwtAudience, string ClientId, bool IsActive);

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
            .Select(candidate => new CurrentApplicationResponse(
                candidate.Id, candidate.Key, candidate.Name, candidate.JwtAudience, candidate.ClientId, candidate.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

        return application is null
            ? ApiProblem.Create(StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized", "A valid access token is required.")
            : Results.Ok(application);
    }
}
