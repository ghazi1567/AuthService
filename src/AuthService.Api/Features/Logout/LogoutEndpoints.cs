namespace AuthService.Api.Features.Logout;

public static class LogoutEndpoints
{
    public const string Route = "/api/auth/logout";

    public static IEndpointRouteBuilder MapLogout(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, LogoutAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> LogoutAsync(
        LogoutRequest request,
        LogoutService logoutService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(LogoutRequest.RefreshToken)] = ["Refresh token is required."],
            });
        }

        await logoutService.LogoutAsync(request.RefreshToken, cancellationToken);

        // The same answer whether or not the token was known, still valid, or already revoked.
        return Results.NoContent();
    }
}
