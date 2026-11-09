using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Tenants;

/// <summary>AUTH-268: a platform admin sees the organisations and suspends or reactivates one. Both routes are under the admin group, so only a platform admin gets this far.</summary>
public static class TenantAdminEndpoints
{
    public const string Route = "/api/auth/admin/tenants";

    public static string StatusRouteFor(Guid tenantId) => $"{Route}/{tenantId}/status";

    public static RouteGroupBuilder MapTenantsAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/tenants", ListAsync);
        admin.MapPut("/tenants/{tenantId:guid}/status", SetStatusAsync);
        return admin;
    }

    /// <summary>Every organisation by name, with its status and how many memberships it has (any status). The built-in product's own data is not an organisation and is not here.</summary>
    private static async Task<IResult> ListAsync(AuthDbContext db, [AsParameters] PageQuery page, CancellationToken cancellationToken)
    {
        if (page.Problem(byName: true) is { } problem)
        {
            return problem;
        }

        var rows = await page.ByNameAsync(
            db.Tenants.AsNoTracking().Select(tenant => new
            {
                tenant.Id,
                tenant.Name,
                tenant.Status,
                MemberCount = db.TenantMemberships.Count(membership => membership.TenantId == tenant.Id),
                tenant.CreatedAt,
            }),
            tenant => tenant.Name,
            tenant => tenant.Id,
            cancellationToken);

        return Results.Ok(rows.Map(items => items.Select(item => new AdminTenantItem(
            item.Id, item.Name, item.Status.ToString(), item.MemberCount, item.CreatedAt)).ToList()));
    }

    private static async Task<IResult> SetStatusAsync(
        Guid tenantId, SetTenantStatusRequest? request, ClaimsPrincipal caller, TenantStatusService tenants, CancellationToken cancellationToken)
    {
        if (!TenantStatusValidator.TryValidate(request, out var status, out var reason, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");

        return await tenants.SetAsync(adminId, tenantId, status, reason, cancellationToken) switch
        {
            SetTenantStatusResult.Changed changed => Results.Ok(changed.Tenant),
            SetTenantStatusResult.Unchanged unchanged => Results.Ok(unchanged.Tenant),
            _ => ApiProblem.Create(StatusCodes.Status404NotFound, "tenant_not_found", "Organisation not found", "No organisation has this id."),
        };
    }
}
