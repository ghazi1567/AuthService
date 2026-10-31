using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Admin;

public sealed record AuditEntryResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Action,
    AuditActorType ActorType,
    Guid? ActorId,
    Guid? TenantId,
    string? SourceIp,
    string? Detail);

/// <summary>Where the next page starts: pass both values back as <c>before</c> and <c>beforeId</c>.</summary>
public sealed record AuditCursor(DateTimeOffset Before, Guid BeforeId);

public sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Items, AuditCursor? Next);

public static class AuditAdminEndpoints
{
    public const string Route = "/api/auth/admin/audit";
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static RouteGroupBuilder MapAuditAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/audit", GetAuditAsync);
        return admin;
    }

    /// <summary>
    /// The platform admin reads the whole audit log, newest first, across every tenant (AUD-01). Filter by action, tenant or
    /// actor; page with the cursor from the previous page. The tenant-scoped read for tenant admins needs tenant tokens and
    /// arrives later.
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

        var query = db.AuditEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(entry => entry.Action == action);
        }

        if (tenantId is { } tenant)
        {
            query = query.Where(entry => entry.TenantId == tenant);
        }

        if (actorId is { } actor)
        {
            query = query.Where(entry => entry.ActorId == actor);
        }

        if (before is { } cutoff)
        {
            query = beforeId is { } cutoffId
                ? query.Where(entry => entry.OccurredAt < cutoff || (entry.OccurredAt == cutoff && entry.Id.CompareTo(cutoffId) < 0))
                : query.Where(entry => entry.OccurredAt < cutoff);
        }

        // One more than asked for, to know whether there is a next page.
        var rows = await query
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        var page = rows.Take(take).ToList();
        var next = rows.Count > take ? new AuditCursor(page[^1].OccurredAt, page[^1].Id) : null;

        // Reading the whole log is an admin action. Recorded after the query, so a read never lists itself.
        if (caller.GetUserId() is { } adminId)
        {
            audit.Record(AuditActions.AdminAuditRead, AuditActor.User(adminId));
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(new AuditPageResponse(
            [.. page.Select(entry => new AuditEntryResponse(
                entry.Id, entry.OccurredAt, entry.Action, entry.ActorType, entry.ActorId, entry.TenantId, entry.SourceIp, entry.Detail))],
            next));
    }
}
