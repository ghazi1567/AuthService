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
    string? Detail,
    string? ActorName = null);

/// <summary>Where the next page starts: pass both values back as <c>before</c> and <c>beforeId</c>.</summary>
public sealed record AuditCursor(DateTimeOffset Before, Guid BeforeId);

public sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Items, AuditCursor? Next);

/// <summary>
/// Reads the audit log newest first, in pages, for both readers: the platform admin across every tenant and a tenant admin within
/// their own. The tenant is passed in by the caller, never read from the request, so the two cannot drift apart.
/// Entries also carry <c>ActorName</c> (AUD-02): who did it, in words. A person is their display name or email, a product is its name.
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
        CancellationToken cancellationToken,
        bool tenantScoped = false)
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

        var names = await ActorNamesAsync(db, page, tenantScoped ? tenantId : null, cancellationToken);
        return new AuditPageResponse(
            [.. page.Select(entry => new AuditEntryResponse(
                entry.Id, entry.OccurredAt, entry.Action, entry.ActorType, entry.ActorId, entry.TenantId, entry.SourceIp, entry.Detail,
                entry.ActorId is { } actor && names.TryGetValue((entry.ActorType, actor), out var name) ? name : null))],
            next);
    }

    /// <summary>
    /// The names of the people and products behind a page of entries, in two small reads. When the reader is an organisation's admin
    /// (<paramref name="onlyMembersOf"/>), a person is named only if they belong to that organisation: someone from another organisation,
    /// or a platform admin acting from outside it, stays "a person" and their email is not shown. A platform admin sees every name.
    /// </summary>
    private static async Task<Dictionary<(AuditActorType Type, Guid Id), string>> ActorNamesAsync(
        AuthDbContext db,
        IReadOnlyList<AuditEntry> page,
        Guid? onlyMembersOf,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<(AuditActorType, Guid), string>();

        var userIds = page.Where(entry => entry.ActorType == AuditActorType.User && entry.ActorId != null).Select(entry => entry.ActorId!.Value).Distinct().ToList();
        if (userIds.Count > 0)
        {
            var users = db.Users.AsNoTracking().Where(user => userIds.Contains(user.Id));
            if (onlyMembersOf is { } tenant)
            {
                users = users.Where(user => db.TenantMemberships.Any(membership => membership.TenantId == tenant && membership.UserId == user.Id));
            }

            foreach (var user in await users.Select(user => new { user.Id, user.DisplayName, user.Email }).ToListAsync(cancellationToken))
            {
                names[(AuditActorType.User, user.Id)] = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName;
            }
        }

        var productIds = page.Where(entry => entry.ActorType == AuditActorType.Product && entry.ActorId != null).Select(entry => entry.ActorId!.Value).Distinct().ToList();
        if (productIds.Count > 0)
        {
            foreach (var product in await db.Applications.AsNoTracking().Where(application => productIds.Contains(application.Id))
                         .Select(application => new { application.Id, application.Name }).ToListAsync(cancellationToken))
            {
                names[(AuditActorType.Product, product.Id)] = product.Name;
            }
        }

        return names;
    }
}
