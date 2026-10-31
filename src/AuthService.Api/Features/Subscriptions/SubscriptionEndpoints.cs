using AuthService.Api.Common;
using AuthService.Api.Tokens;

namespace AuthService.Api.Features.Subscriptions;

public static class SubscriptionEndpoints
{
    public const string Route = "/api/auth/tenants/{tenantId:guid}/subscriptions";

    public static string RouteFor(Guid tenantId) => $"/api/auth/tenants/{tenantId}/subscriptions";

    public static IEndpointRouteBuilder MapSubscriptions(this IEndpointRouteBuilder app)
    {
        // Called by the product itself (service token). The product is always the caller's own, from the verified token.
        app.MapPost(Route, SubscribeAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        return app;
    }

    private static async Task<IResult> SubscribeAsync(
        Guid tenantId,
        SubscribeRequest? request,
        System.Security.Claims.ClaimsPrincipal caller,
        SubscribeTenantService subscriptions,
        CancellationToken cancellationToken)
    {
        if (request?.MaxSeats is < 1)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(SubscribeRequest.MaxSeats)] = ["MaxSeats must be at least 1, or left out for unlimited seats."],
            });
        }

        var applicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        var result = await subscriptions.SubscribeAsync(applicationId, tenantId, request?.MaxSeats, cancellationToken);

        return result switch
        {
            SubscribeResult.Subscribed { IsNew: true } created => Results.Json(created.Subscription, statusCode: StatusCodes.Status201Created),
            SubscribeResult.Subscribed reactivated => Results.Ok(reactivated.Subscription),
            SubscribeResult.AlreadySubscribed existing => Results.Ok(existing.Subscription),

            SubscribeResult.ApplicationNotActive => ApiProblem.Create(StatusCodes.Status400BadRequest, "application_not_active",
                "Application not active", "This application is not active, so tenants cannot subscribe to it."),

            SubscribeResult.TenantSuspended => ApiProblem.Create(StatusCodes.Status409Conflict, "tenant_suspended",
                "Tenant suspended", "A suspended tenant cannot subscribe to an application."),

            _ => ApiProblem.Create(StatusCodes.Status404NotFound, "tenant_not_found", "Tenant not found", "No tenant has this id."),
        };
    }
}
