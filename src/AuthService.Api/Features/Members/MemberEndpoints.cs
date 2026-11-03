using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Members;

public static class MemberEndpoints
{
    public const string Route = "/api/auth/tenants/me/members";

    public const string MemberRoute = "/api/auth/tenants/me/members/{membershipId:guid}";

    public static string MemberRouteFor(Guid membershipId) => $"/api/auth/tenants/me/members/{membershipId}";

    public static IEndpointRouteBuilder MapMembers(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, ListAsync).RequireTenantPermission(PermissionKeys.MembersRead, AccessLevel.ReadOnly);
        app.MapPatch(MemberRoute, SetStatusAsync).RequireTenantPermission(PermissionKeys.MembersManage, AccessLevel.Full);
        return app;
    }

    /// <summary>
    /// Lists the members of the caller's tenant by email, one page at a time (PLT-08). The tenant is the token's, so nobody from another
    /// tenant is ever shown.
    /// </summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal caller, MembersService members, [AsParameters] PageQuery page, CancellationToken cancellationToken)
    {
        if (page.Problem(byName: true) is { } problem)
        {
            return problem;
        }

        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        return Results.Ok(await members.ListAsync(tenantId, page, cancellationToken));
    }

    /// <summary>Enables or disables a member's membership in the caller's tenant, and only there.</summary>
    private static async Task<IResult> SetStatusAsync(
        Guid membershipId, SetMemberStatusRequest request, ClaimsPrincipal caller, MembersService members, CancellationToken cancellationToken)
    {
        if (!MemberStatusValidator.TryRead(request, out var status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(SetMemberStatusRequest.Status)] = ["Status must be Active or Disabled."],
            });
        }

        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var result = await members.SetStatusAsync(userId, tenantId, membershipId, status, cancellationToken);

        return result switch
        {
            SetMemberStatusResult.Updated updated => Results.Ok(updated.Member),
            SetMemberStatusResult.Unchanged unchanged => Results.Ok(unchanged.Member),

            SetMemberStatusResult.StillInvited => ApiProblem.Create(StatusCodes.Status409Conflict, "membership_invited", "Invitation not accepted",
                "This person has not accepted their invitation yet. They become Active by accepting it."),

            SetMemberStatusResult.OwnerRoleRestricted => ApiProblem.Create(StatusCodes.Status403Forbidden, "owner_role_restricted", "Owner role restricted",
                "Only an Owner can disable an Owner."),

            SetMemberStatusResult.WouldLeaveNoOwner => ApiProblem.Create(StatusCodes.Status409Conflict, "last_owner", "The tenant needs an Owner",
                "This would leave the tenant without an active Owner. Make someone else an Owner first."),

            SetMemberStatusResult.NoSeatAvailable full => ApiProblem.Create(StatusCodes.Status409Conflict, "no_seat_available", "No seat available",
                $"The member cannot be enabled because there is no free seat in: {string.Join(", ", full.ApplicationKeys)}."),

            _ => ApiProblem.Create(StatusCodes.Status404NotFound, "not_found", "Not found", "No such member in this tenant."),
        };
    }
}
