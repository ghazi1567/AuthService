using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Subscriptions;

public static class MemberAccessEndpoints
{
    public const string Route = "/api/auth/tenants/me/members/{membershipId:guid}/applications/{applicationId:guid}";

    public static string RouteFor(Guid membershipId, Guid applicationId) =>
        $"/api/auth/tenants/me/members/{membershipId}/applications/{applicationId}";

    public static IEndpointRouteBuilder MapMemberAccess(this IEndpointRouteBuilder app)
    {
        app.MapPut(Route, GrantAsync).RequireTenantPermission(PermissionKeys.AccessManage, AccessLevel.Full);
        app.MapDelete(Route, RevokeAsync).RequireTenantPermission(PermissionKeys.AccessManage, AccessLevel.Full);
        return app;
    }

    private static async Task<IResult> GrantAsync(
        Guid membershipId, Guid applicationId, ClaimsPrincipal caller, MemberAccessService access, CancellationToken cancellationToken)
    {
        // Who and which tenant come only from the verified token, never from the request.
        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var result = await access.GrantAsync(userId, tenantId, membershipId, applicationId, cancellationToken);

        return result switch
        {
            GrantAccessResult.Granted granted => Results.Json(granted.Access, statusCode: StatusCodes.Status201Created),
            GrantAccessResult.AlreadyGranted existing => Results.Ok(existing.Access),

            GrantAccessResult.NotSubscribed => ApiProblem.Create(StatusCodes.Status400BadRequest, "application_not_subscribed",
                "Application not subscribed", "The tenant has no active subscription to this application."),

            GrantAccessResult.MembershipDisabled => ApiProblem.Create(StatusCodes.Status409Conflict, "membership_disabled",
                "Membership disabled", "A disabled member cannot be given access. Enable the membership first."),

            GrantAccessResult.NoSeatAvailable => ApiProblem.Create(StatusCodes.Status409Conflict, "no_seat_available",
                "No seat available", "All seats of this subscription are in use."),

            _ => MembershipNotFound(),
        };
    }

    private static async Task<IResult> RevokeAsync(
        Guid membershipId, Guid applicationId, ClaimsPrincipal caller, MemberAccessService access, CancellationToken cancellationToken)
    {
        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var result = await access.RevokeAsync(userId, tenantId, membershipId, applicationId, cancellationToken);

        return result is RevokeAccessResult.MembershipNotFound ? MembershipNotFound() : Results.NoContent();
    }

    private static IResult MembershipNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "not_found", "Not found", "No such member in this tenant.");
}
