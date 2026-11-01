using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;

namespace AuthService.Api.Features.Admin;

public static class AuditAdminEndpoints
{
    public const string Route = "/api/auth/admin/audit";
    public const int DefaultLimit = AuditPaging.DefaultLimit;
    public const int MaxLimit = AuditPaging.MaxLimit;

    public static RouteGroupBuilder MapAuditAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/audit", GetAuditAsync);
        return admin;
    }

    /// <summary>
    /// The platform admin reads the whole audit log, newest first, across every tenant (AUD-01). Filter by action, tenant or
    /// actor; page with the cursor from the previous page. Tenant admins read their own tenant's entries elsewhere.
    /// </summary>
    private static async Task<IResult> GetAuditAsync(
        ClaimsPrincipal caller,
        AuthDbContext db,
        AuditLog audit,
        string? action,
        Guid? tenantId,
        Guid? actorId,
        DateTimeOffset? before,
        Guid? beforeId,
        int? limit,
        CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(limit)] = [$"Limit must be between 1 and {MaxLimit}."],
            });
        }

        var page = await AuditPaging.PageAsync(db, tenantId, action, actorId, before, beforeId, take, cancellationToken);

        // Reading the whole log is an admin action. Recorded after the query, so a read never lists itself.
        if (caller.GetUserId() is { } adminId)
        {
            audit.Record(AuditActions.AdminAuditRead, AuditActor.User(adminId));
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(page);
    }
}
