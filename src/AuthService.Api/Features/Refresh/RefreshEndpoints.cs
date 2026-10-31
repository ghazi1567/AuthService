using AuthService.Api.Common;

namespace AuthService.Api.Features.Refresh;

public static class RefreshEndpoints
{
    public const string Route = "/api/auth/refresh";

    public static IEndpointRouteBuilder MapRefresh(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, RefreshAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        RefreshService refreshService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(RefreshRequest.RefreshToken)] = ["Refresh token is required."],
            });
        }

        var result = await refreshService.RefreshAsync(request.RefreshToken, cancellationToken);

        // Responses carry credentials, so they must never be stored by a browser or proxy.
        httpContext.Response.Headers.CacheControl = "no-store";

        return result switch
        {
            RefreshResult.Succeeded succeeded => Results.Ok(succeeded.Response),

            RefreshResult.AccountDisabled => ApiProblem.Create(StatusCodes.Status403Forbidden, "account_disabled",
                "Account disabled", "This account has been disabled."),

            // One response for an unknown, expired, revoked or reused token, so it reveals nothing about which it was.
            _ => ApiProblem.Create(StatusCodes.Status401Unauthorized, "invalid_refresh_token",
                "Invalid refresh token", "The refresh token is invalid, expired or has already been used."),
        };
    }
}
