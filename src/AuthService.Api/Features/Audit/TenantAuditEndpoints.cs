using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Audit;

/// <summary>AUD-01: a tenant admin reads their own tenant's audit entries.</summary>
public static class TenantAuditEndpoints
{
    public const string Route = "/api/auth/tenants/me/audit";

    public static IEndpointRouteBuilder MapTenantAudit(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, GetAsync).RequireTenantPermission(PermissionKeys.AuditRead, AccessLevel.ReadOnly);
        return app;
    }

    /// <summary>
    /// Newest first, filtered by action or actor and paged with the cursor of the previous page, exactly like the platform admin's
    /// read. The tenant is the token's <c>tid</c> and nothing else: a <c>tenantId</c> in the query string is never read, so a tenant
    /// admin can see only entries that concern their own tenant.
    /// </summary>
    private static async Task<IResult> GetAsync(
        ClaimsPrincipal caller,
        AuthDbContext db,
        AuditLog audit,
        string? action,
        Guid? actorId,
        DateTimeOffset? before,
        Guid? beforeId,
        int? limit,
        CancellationToken cancellationToken)
    {
        var take = limit ?? AuditPaging.DefaultLimit;
        if (take is < 1 or > AuditPaging.MaxLimit)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(limit)] = [$"Limit must be between 1 and {AuditPaging.MaxLimit}."],
            });
        }

        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var page = await AuditPaging.PageAsync(db, tenantId, action, actorId, before, beforeId, take, cancellationToken, tenantScoped: true);

        // Reading the log is itself traced, as it is for the platform admin. Recorded after the query, so a read never lists itself.
        audit.Record(AuditActions.TenantAuditRead, AuditActor.User(userId), tenantId);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(page);
    }
}
