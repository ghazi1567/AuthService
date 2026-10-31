using AuthService.Api.Common;
using AuthService.Api.Tokens;

namespace AuthService.Api.Features.Subscriptions;

public static class SubscriptionEndpoints
{
    public const string Route = "/api/auth/tenants/{tenantId:guid}/subscriptions";

    public const string SeatsRoute = "/api/auth/tenants/{tenantId:guid}/subscriptions/{applicationId:guid}/seats";

    public const string StatusRoute = "/api/auth/tenants/{tenantId:guid}/subscriptions/{applicationId:guid}";

    public static string StatusRouteFor(Guid tenantId, Guid applicationId) => $"/api/auth/tenants/{tenantId}/subscriptions/{applicationId}";

    public static string RouteFor(Guid tenantId) => $"/api/auth/tenants/{tenantId}/subscriptions";

    public static string SeatsRouteFor(Guid tenantId, Guid applicationId) => $"/api/auth/tenants/{tenantId}/subscriptions/{applicationId}/seats";

    public static IEndpointRouteBuilder MapSubscriptions(this IEndpointRouteBuilder app)
    {
        // Called by the product itself (service token). The product is always the caller's own, from the verified token.
        app.MapPost(Route, SubscribeAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        app.MapPatch(SeatsRoute, SetSeatLimitAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        app.MapPatch(StatusRoute, SetStatusAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        return app;
    }

    /// <summary>
    /// A product suspends or cancels a tenant's subscription (SUB-02). Like the seat limit, the application in the path must be
    /// the caller's own, and anything else is reported as not found.
    /// </summary>
    private static async Task<IResult> SetStatusAsync(
        Guid tenantId,
        Guid applicationId,
        SetSubscriptionStatusRequest? request,
        System.Security.Claims.ClaimsPrincipal caller,
        SubscriptionStatusService statuses,
        CancellationToken cancellationToken)
    {
        if (!SubscriptionStatusValidator.TryRead(request, out var status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(SetSubscriptionStatusRequest.Status)] = ["Status must be Suspended or Cancelled. To make a subscription Active again, subscribe the tenant again."],
            });
        }

        var callerApplicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        if (applicationId != callerApplicationId)
        {
            return SubscriptionNotFound();
        }

        var result = await statuses.SetAsync(applicationId, tenantId, status, cancellationToken);

        return result switch
        {
            SetSubscriptionStatusResult.Updated updated => Results.Ok(updated.Subscription),
            SetSubscriptionStatusResult.Unchanged unchanged => Results.Ok(unchanged.Subscription),

            SetSubscriptionStatusResult.AlreadyCancelled => ApiProblem.Create(StatusCodes.Status409Conflict, "subscription_cancelled",
                "Subscription cancelled", "A cancelled subscription cannot be suspended. Subscribe the tenant again to start a new one."),

            _ => SubscriptionNotFound(),
        };
    }

    /// <summary>
    /// A product sets the seat limit of one of its tenants' subscriptions (SUB-04). The application in the path must be the
    /// caller's own: another product's subscription is reported as not found, exactly like one that does not exist.
    /// </summary>
    private static async Task<IResult> SetSeatLimitAsync(
        Guid tenantId,
        Guid applicationId,
        System.Text.Json.JsonElement body,
        System.Security.Claims.ClaimsPrincipal caller,
        SeatLimitService seatLimits,
        CancellationToken cancellationToken)
    {
        if (!SeatLimitValidator.TryRead(body, out var maxSeats, out var error))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["maxSeats"] = [error!] });
        }

        var callerApplicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        if (applicationId != callerApplicationId)
        {
            return SubscriptionNotFound();
        }

        var result = await seatLimits.SetAsync(applicationId, tenantId, maxSeats, cancellationToken);

        return result switch
        {
            SetSeatLimitResult.Updated updated => Results.Ok(updated.Subscription),
            _ => SubscriptionNotFound(),
        };
    }

    private static IResult SubscriptionNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "subscription_not_found", "Subscription not found", "No such subscription.");

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
