using AuthService.Api.Common;
using AuthService.Api.Tokens;

namespace AuthService.Api.Features.TokenExchange;

public static class TokenExchangeEndpoints
{
    public const string Route = "/api/auth/token/exchange";

    public const string RateLimitPolicy = "token-exchange";

    public static IEndpointRouteBuilder MapTokenExchange(this IEndpointRouteBuilder app)
    {
        // Needs an identity token: a product token or service token cannot be traded for another one.
        app.MapPost(Route, ExchangeAsync).RequireAuthorization(IdentityAuthentication.PolicyName).RequireRateLimiting(RateLimitPolicy);
        return app;
    }

    private static async Task<IResult> ExchangeAsync(
        TokenExchangeRequest request,
        System.Security.Claims.ClaimsPrincipal caller,
        TokenExchangeService exchange,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.TenantId is null || request.TenantId == Guid.Empty)
        {
            errors[nameof(TokenExchangeRequest.TenantId)] = ["TenantId is required."];
        }

        if (request.ApplicationId is null || request.ApplicationId == Guid.Empty)
        {
            errors[nameof(TokenExchangeRequest.ApplicationId)] = ["ApplicationId is required."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Who is asking comes only from the verified identity token, never from the request.
        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The identity-token policy guarantees a user id.");
        var result = await exchange.ExchangeAsync(userId, request.TenantId!.Value, request.ApplicationId!.Value, cancellationToken);

        httpContext.Response.Headers.CacheControl = "no-store";

        return result switch
        {
            TokenExchangeResult.Issued issued => Results.Ok(issued.Response),

            TokenExchangeResult.TenantSuspended => ApiProblem.Create(StatusCodes.Status403Forbidden, "tenant_suspended", "Organisation suspended",
                "This organisation is suspended, so nothing can be opened in it right now."),

            _ => ApiProblem.Create(StatusCodes.Status403Forbidden, "exchange_denied", "Access denied",
                "You do not have access to this application in this tenant."),
        };
    }
}
