using AuthService.Api.Common;

namespace AuthService.Api.Features.Login;

public static class LoginEndpoints
{
    public const string Route = "/api/auth/login";

    public static IEndpointRouteBuilder MapLogin(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, LoginAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        LoginService loginService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            errors[nameof(LoginRequest.Email)] = ["Email is required."];
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            errors[nameof(LoginRequest.Password)] = ["Password is required."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await loginService.LoginAsync(EmailNormalizer.Normalize(request.Email!), request.Password!, cancellationToken);

        // Responses carry credentials, so they must never be stored by a browser or proxy.
        httpContext.Response.Headers.CacheControl = "no-store";

        return result switch
        {
            LoginResult.Succeeded succeeded => Results.Ok(succeeded.Response),

            LoginResult.AccountDisabled => Problem(StatusCodes.Status403Forbidden, "account_disabled", "Account disabled",
                "This account has been disabled."),

            LoginResult.EmailNotVerified => Problem(StatusCodes.Status403Forbidden, "email_not_verified", "Email not verified",
                "Verify your email address before logging in."),

            // One response for every kind of bad credentials, so it never reveals whether an email exists.
            _ => Problem(StatusCodes.Status401Unauthorized, "invalid_credentials", "Invalid credentials",
                "The email or password is incorrect."),
        };
    }

    private static IResult Problem(int statusCode, string code, string title, string detail) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
