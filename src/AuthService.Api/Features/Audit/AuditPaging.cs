using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Audit;

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

/// <summary>
/// Reads the audit log newest first, in pages, for both readers: the platform admin across every tenant and a tenant admin within
/// their own. The tenant is passed in by the caller, never read from the request, so the two cannot drift apart.
/// </summary>
public static class AuditPaging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static async Task<AuditPageResponse> PageAsync(
        AuthDbContext db,
        Guid? tenantId,
        string? action,
        Guid? actorId,
        DateTimeOffset? before,
        Guid? beforeId,
        int take,
        CancellationToken cancellationToken)
    {
        var query = db.AuditEntries.AsNoTracking().AsQueryable();

        if (tenantId is { } tenant)
        {
            query = query.Where(entry => entry.TenantId == tenant);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(entry => entry.Action == action);
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

        return new AuditPageResponse(
            [.. page.Select(entry => new AuditEntryResponse(
                entry.Id, entry.OccurredAt, entry.Action, entry.ActorType, entry.ActorId, entry.TenantId, entry.SourceIp, entry.Detail))],
            next);
    }
}
