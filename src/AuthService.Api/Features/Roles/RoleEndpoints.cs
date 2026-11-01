using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Roles;

public static class RoleEndpoints
{
    public const string Route = "/api/auth/roles";

    public static IEndpointRouteBuilder MapRoles(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, CreateAsync).RequireTenantPermission(PermissionKeys.RolesManage, AccessLevel.Full);
        app.MapPut(MemberRolesRoute, AssignAsync).RequireTenantPermission(PermissionKeys.RolesAssign, AccessLevel.Full);
        return app;
    }

    public const string MemberRolesRoute = "/api/auth/tenants/me/members/{membershipId:guid}/roles";

    public static string MemberRolesRouteFor(Guid membershipId) => $"/api/auth/tenants/me/members/{membershipId}/roles";

    /// <summary>Replaces the roles a member of the caller's tenant holds (ROL-05).</summary>
    private static async Task<IResult> AssignAsync(
        Guid membershipId,
        AssignRolesRequest request,
        ClaimsPrincipal caller,
        AssignRolesService assign,
        CancellationToken cancellationToken)
    {
        if (!AssignRolesValidator.TryValidate(request, out var roleIds, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        // The tenant comes from the token: a membership of another tenant is simply not found.
        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var result = await assign.AssignAsync(userId, tenantId, membershipId, roleIds, cancellationToken);

        return result switch
        {
            AssignRolesResult.Assigned assigned => Results.Ok(assigned.Response),

            AssignRolesResult.EmptySet => ApiProblem.Create(StatusCodes.Status409Conflict, "roles_required", "A member needs a role",
                "A member must keep at least one role. To remove their access, disable the membership instead."),

            AssignRolesResult.OwnerRoleRestricted => ApiProblem.Create(StatusCodes.Status403Forbidden, "owner_role_restricted", "Owner role restricted",
                "Only an Owner can give or remove the Owner role."),

            AssignRolesResult.WouldLeaveNoOwner => ApiProblem.Create(StatusCodes.Status409Conflict, "last_owner", "The tenant needs an Owner",
                "This would leave the tenant without an active Owner. Make someone else an Owner first."),

            _ => ApiProblem.Create(StatusCodes.Status404NotFound, "not_found", "Not found", "The member or a role was not found in this tenant."),
        };
    }

    private static async Task<IResult> CreateAsync(
        CreateRoleRequest request,
        ClaimsPrincipal caller,
        CreateRoleService roles,
        CancellationToken cancellationToken)
    {
        if (!RoleValidator.TryValidate(request, out var valid, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        // Who and which tenant come only from the verified token, never from the request.
        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var result = await roles.CreateAsync(userId, tenantId, valid!, cancellationToken);

        return result switch
        {
            CreateRoleResult.Created created => Results.Json(created.Role, statusCode: StatusCodes.Status201Created),

            CreateRoleResult.InvalidGrants invalid => Results.ValidationProblem(invalid.Errors),

            _ => ApiProblem.Create(StatusCodes.Status409Conflict, "role_name_taken", "Role name already used",
                "This tenant already has a role with this name."),
        };
    }
}
