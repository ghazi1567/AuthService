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
        app.MapGet(Route, ListAsync).RequireTenantPermission(PermissionKeys.RolesRead, AccessLevel.ReadOnly);
        app.MapPatch(RoleRoute, UpdateAsync).RequireTenantPermission(PermissionKeys.RolesManage, AccessLevel.Full);
        app.MapDelete(RoleRoute, DeleteAsync).RequireTenantPermission(PermissionKeys.RolesManage, AccessLevel.Full);
        app.MapPut(MemberRolesRoute, AssignAsync).RequireTenantPermission(PermissionKeys.RolesAssign, AccessLevel.Full);
        return app;
    }

    public const string RoleRoute = "/api/auth/roles/{roleId:guid}";

    public static string RoleRouteFor(Guid roleId) => $"/api/auth/roles/{roleId}";

    /// <summary>
    /// Lists the roles of the caller's tenant (ROL-02), one page at a time (PLT-08). The tenant is the token's, so no other tenant's role
    /// is ever shown.
    /// </summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal caller, ManageRolesService roles, [AsParameters] PageQuery page, CancellationToken cancellationToken)
    {
        if (page.Problem(byName: true) is { } problem)
        {
            return problem;
        }

        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        return Results.Ok(await roles.ListAsync(tenantId, page, cancellationToken));
    }

    /// <summary>Changes a role's name and/or the permissions it grants (ROL-03). Holders see it at their next token exchange.</summary>
    private static async Task<IResult> UpdateAsync(
        Guid roleId, UpdateRoleRequest request, ClaimsPrincipal caller, ManageRolesService roles, CancellationToken cancellationToken)
    {
        if (!UpdateRoleValidator.TryValidate(request, out var valid, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var result = await roles.UpdateAsync(userId, tenantId, roleId, valid!, cancellationToken);

        return result switch
        {
            UpdateRoleResult.Updated updated => Results.Ok(updated.Role),
            UpdateRoleResult.InvalidGrants invalid => Results.ValidationProblem(invalid.Errors),
            UpdateRoleResult.OwnerRoleImmutable => OwnerRoleImmutable(),

            UpdateRoleResult.NameTaken => ApiProblem.Create(StatusCodes.Status409Conflict, "role_name_taken", "Role name already used",
                "This tenant already has a role with this name."),

            _ => RoleNotFound(),
        };
    }

    /// <summary>Deletes a role nobody holds (ROL-04).</summary>
    private static async Task<IResult> DeleteAsync(Guid roleId, ClaimsPrincipal caller, ManageRolesService roles, CancellationToken cancellationToken)
    {
        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var result = await roles.DeleteAsync(userId, tenantId, roleId, cancellationToken);

        return result switch
        {
            DeleteRoleResult.Deleted => Results.NoContent(),
            DeleteRoleResult.OwnerRoleImmutable => OwnerRoleImmutable(),

            DeleteRoleResult.InUse => ApiProblem.Create(StatusCodes.Status409Conflict, "role_in_use", "Role in use",
                "Members still hold this role. Change their roles first."),

            _ => RoleNotFound(),
        };
    }

    // 404, never 403, for a role of another tenant, so its existence is not confirmed.
    private static IResult RoleNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "not_found", "Not found", "No such role in this tenant.");

    private static IResult OwnerRoleImmutable() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, "owner_role_immutable", "The Owner role cannot be changed",
            "The built-in Owner role can never be edited, downgraded or deleted.");

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
