using AuthService.Api.RateLimiting;
using AuthService.Api.Common;

namespace AuthService.Api.Features.EmailVerification;

public static class EmailVerificationEndpoints
{
    public const string VerifyRoute = "/api/auth/email/verify";
    public const string ResendRoute = "/api/auth/email/verify/resend";
    public const string ResendRateLimitPolicy = "email-verification-resend";

    public static IEndpointRouteBuilder MapEmailVerification(this IEndpointRouteBuilder app)
    {
        app.MapPost(VerifyRoute, VerifyAsync).AllowAnonymous();
        app.MapPost(ResendRoute, ResendAsync).AllowAnonymous().RequireSharedRateLimit(ResendRateLimitPolicy);
        return app;
    }

    private static async Task<IResult> VerifyAsync(
        VerifyEmailRequest request,
        EmailVerificationService verification,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(VerifyEmailRequest.Token)] = ["Token is required."],
            });
        }

        var result = await verification.VerifyAsync(request.Token, cancellationToken);

        return result switch
        {
            VerifyEmailResult.Verified or VerifyEmailResult.AlreadyVerified => Results.Ok(new VerifyEmailResponse(true)),

            // One answer for an unknown, expired or used link, so it reveals nothing about which it was.
            _ => ApiProblem.Create(StatusCodes.Status410Gone, "verification_link_invalid",
                "Verification link invalid", "This verification link is invalid or has expired. Request a new one."),
        };
    }

    private static async Task<IResult> ResendAsync(
        ResendVerificationRequest request,
        EmailVerificationService verification,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(ResendVerificationRequest.Email)] = ["Email is required."],
            });
        }

        await verification.ResendAsync(EmailNormalizer.Normalize(request.Email), cancellationToken);

        // The same response whether or not the address is registered, verified, or was just sent a link.
        return Results.Accepted(
            uri: null,
            value: new ResendVerificationResponse("If that email belongs to an unverified account, a new verification link has been sent."));
    }
}
