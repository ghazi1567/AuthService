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
        return app;
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
