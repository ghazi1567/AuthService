using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Tokens;

namespace AuthService.Api.Features.CurrentUser;

public static class CurrentUserEndpoints
{
    public const string Route = "/api/auth/users/me";

    public static IEndpointRouteBuilder MapCurrentUser(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, GetCurrentUserAsync).RequireAuthorization(IdentityAuthentication.PolicyName);
        return app;
    }

    // Takes no id from the path or query string: who the caller is comes only from the verified token.
    private static async Task<IResult> GetCurrentUserAsync(
        ClaimsPrincipal caller,
        CurrentUserService currentUser,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var profile = caller.GetUserId() is { } userId
            ? await currentUser.GetAsync(userId, cancellationToken)
            : null;

        if (profile is null)
        {
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized",
                "A valid access token is required.");
        }

        // Personal data: never to be stored by a shared cache.
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(profile);
    }
}
