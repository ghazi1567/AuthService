using AuthService.Api.Common;

namespace AuthService.Api.Features.PasswordReset;

public static class PasswordResetEndpoints
{
    public const string ForgotRoute = "/api/auth/password/forgot";
    public const string ResetRoute = "/api/auth/password/reset";
    public const string RateLimitPolicy = "password-reset";

    public static IEndpointRouteBuilder MapPasswordReset(this IEndpointRouteBuilder app)
    {
        app.MapPost(ForgotRoute, ForgotAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        app.MapPost(ResetRoute, ResetAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        return app;
    }

    private static async Task<IResult> ForgotAsync(
        ForgotPasswordRequest request,
        PasswordResetService passwordReset,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(ForgotPasswordRequest.Email)] = ["Email is required."],
            });
        }

        await passwordReset.RequestAsync(EmailNormalizer.Normalize(request.Email), cancellationToken);

        // The same response whether or not the address is registered, or was just sent a link.
        return Results.Accepted(
            uri: null,
            value: new ForgotPasswordResponse("If that email belongs to an account, a password reset link has been sent."));
    }

    private static async Task<IResult> ResetAsync(
        ResetPasswordRequest request,
        PasswordResetService passwordReset,
        CancellationToken cancellationToken)
    {
        // Checked before the link is touched, so a rejected password does not use the link up.
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            errors[nameof(ResetPasswordRequest.Token)] = ["Token is required."];
        }

        if (PasswordPolicy.Validate(request.NewPassword) is { } passwordError)
        {
            errors[nameof(ResetPasswordRequest.NewPassword)] = [passwordError];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await passwordReset.ResetAsync(request.Token!, request.NewPassword!, cancellationToken);

        return result switch
        {
            ResetPasswordResult.Reset => Results.Ok(new ResetPasswordResponse(true)),

            // One answer for an unknown, expired or used link, so it reveals nothing about which it was.
            _ => ApiProblem.Create(StatusCodes.Status410Gone, "reset_link_invalid",
                "Reset link invalid", "This password reset link is invalid or has expired. Request a new one."),
        };
    }
}
