using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Members;

public sealed record MemberRoleDto(Guid Id, string Name);

public sealed record MemberApplicationDto(Guid ApplicationId, string ApplicationKey);

/// <summary>A person's membership in the caller's tenant, with the roles they hold and the products they have a seat in.</summary>
public sealed record MemberItem(
    Guid MembershipId,
    string Email,
    string? DisplayName,
    string Status,
    IReadOnlyList<MemberRoleDto> Roles,
    IReadOnlyList<MemberApplicationDto> Applications,
    DateTimeOffset CreatedAt);

/// <summary>Body of <c>PATCH /api/auth/tenants/me/members/{membershipId}</c>.</summary>
public sealed record SetMemberStatusRequest(string? Status);

public abstract record SetMemberStatusResult
{
    public sealed record Updated(MemberItem Member) : SetMemberStatusResult;

    /// <summary>The membership already had this status: nothing changed.</summary>
    public sealed record Unchanged(MemberItem Member) : SetMemberStatusResult;

    /// <summary>Not in the caller's tenant, or does not exist. Never a 403, so ids are not confirmed.</summary>
    public sealed record NotFound : SetMemberStatusResult;

    /// <summary>An invited person becomes Active by accepting the invitation (TEN-08), not by being enabled.</summary>
    public sealed record StillInvited : SetMemberStatusResult;

    public sealed record OwnerRoleRestricted : SetMemberStatusResult;

    public sealed record WouldLeaveNoOwner : SetMemberStatusResult;

    /// <summary>Re-enabling would put a product over its seat limit. The keys say which.</summary>
    public sealed record NoSeatAvailable(IReadOnlyList<string> ApplicationKeys) : SetMemberStatusResult;
}

public static class MemberStatusValidator
{
    /// <summary>Only Active and Disabled can be set: Invited is where a membership starts, not somewhere it can be put.</summary>
    public static bool TryRead(SetMemberStatusRequest request, out MembershipStatus status)
    {
        status = default;
        var text = request.Status?.Trim();
        if (string.IsNullOrEmpty(text) || text.All(char.IsDigit) || !Enum.TryParse(text, ignoreCase: true, out MembershipStatus parsed)
            || !Enum.IsDefined(parsed) || parsed == MembershipStatus.Invited)
        {
            return false;
        }

        status = parsed;
        return true;
    }
}

/// <summary>TEN-07: the members of the caller's tenant, and enabling or disabling their membership.</summary>
public sealed class MembersService(AuthDbContext db, AuditLog audit)
{
    /// <summary>One page of the members, by email (PLT-08).</summary>
    public async Task<PagedResponse<MemberItem, NameCursor>> ListAsync(Guid tenantId, PageQuery page, CancellationToken cancellationToken)
    {
        var rows = await page.ByNameAsync(MemberRows(tenantId), row => row.Email, row => row.Id, cancellationToken);
        var members = await ToItemsAsync(tenantId, rows.Rows, cancellationToken);
        return new PagedResponse<MemberItem, NameCursor>(members, rows.Next);
    }

    public async Task<SetMemberStatusResult> SetStatusAsync(
        Guid callerUserId, Guid tenantId, Guid membershipId, MembershipStatus status, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Membership changes in one tenant take turns, like role changes, so "the tenant keeps an Owner" holds when admins act at once.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM tenants WHERE id = {tenantId} FOR UPDATE", cancellationToken);

        var membership = await db.TenantMemberships.SingleOrDefaultAsync(
            candidate => candidate.Id == membershipId && candidate.TenantId == tenantId, cancellationToken);
        if (membership is null)
        {
            return new SetMemberStatusResult.NotFound();
        }

        if (membership.Status == status)
        {
            return new SetMemberStatusResult.Unchanged((await LoadAsync(tenantId, membershipId, cancellationToken)).Single());
        }

        if (status == MembershipStatus.Active && membership.Status == MembershipStatus.Invited)
        {
            return new SetMemberStatusResult.StillInvited();
        }

        var ownerRoleId = await db.Roles
            .Where(role => role.TenantId == tenantId && role.IsBuiltIn && role.Name == Role.OwnerName)
            .Select(role => role.Id)
            .SingleAsync(cancellationToken);

        if (status == MembershipStatus.Disabled)
        {
            var targetIsOwner = await db.MembershipRoles.AnyAsync(
                assignment => assignment.MembershipId == membershipId && assignment.RoleId == ownerRoleId, cancellationToken);
            if (targetIsOwner && membership.Status == MembershipStatus.Active)
            {
                // Disabling an Owner is reserved to Owners, like giving or removing the role, and the tenant must keep an active one.
                if (!await IsActiveOwnerAsync(tenantId, ownerRoleId, callerUserId, cancellationToken))
                {
                    return new SetMemberStatusResult.OwnerRoleRestricted();
                }

                var otherActiveOwners = await (
                    from assignment in db.MembershipRoles
                    join other in db.TenantMemberships on new { assignment.TenantId, assignment.MembershipId } equals new { other.TenantId, MembershipId = other.Id }
                    where assignment.TenantId == tenantId && assignment.RoleId == ownerRoleId
                        && assignment.MembershipId != membershipId && other.Status == MembershipStatus.Active
                    select assignment.Id)
                    .AnyAsync(cancellationToken);
                if (!otherActiveOwners)
                {
                    return new SetMemberStatusResult.WouldLeaveNoOwner();
                }
            }
        }
        else
        {
            // Re-enabling puts the member's seats back in use, so every product they have access to is checked against its limit,
            // under the same row lock grants take. Locked in a fixed order so two re-enablements cannot wait on each other.
            var applicationIds = await db.ApplicationAccess
                .Where(access => access.MembershipId == membershipId)
                .Select(access => access.ApplicationId)
                .OrderBy(id => id)
                .ToListAsync(cancellationToken);

            var full = new List<string>();
            foreach (var applicationId in applicationIds)
            {
                var subscription = await db.Subscriptions
                    .FromSqlInterpolated($"SELECT * FROM subscriptions WHERE tenant_id = {tenantId} AND application_id = {applicationId} FOR UPDATE")
                    .AsNoTracking()
                    .SingleOrDefaultAsync(cancellationToken);
                if (subscription?.MaxSeats is not { } maxSeats)
                {
                    continue;
                }

                // The member is Disabled, so they are not in this count yet.
                var seatsUsed = await db.SeatsUsedAsync(tenantId, applicationId, cancellationToken);
                if (seatsUsed >= maxSeats)
                {
                    full.Add(await db.Applications.AsNoTracking().Where(application => application.Id == applicationId)
                        .Select(application => application.Key).SingleAsync(cancellationToken));
                }
            }

            if (full.Count > 0)
            {
                return new SetMemberStatusResult.NoSeatAvailable([.. full.Order(StringComparer.Ordinal)]);
            }
        }

        // Only the status changes. Roles, access and history stay, so enabling again restores the member as they were.
        membership.Status = status;
        audit.Record(
            status == MembershipStatus.Disabled ? AuditActions.MemberDisabled : AuditActions.MemberEnabled,
            AuditActor.User(callerUserId), tenantId, membershipId.ToString());

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SetMemberStatusResult.Updated((await LoadAsync(tenantId, membershipId, cancellationToken)).Single());
    }

    private Task<bool> IsActiveOwnerAsync(Guid tenantId, Guid ownerRoleId, Guid userId, CancellationToken cancellationToken) =>
        (from assignment in db.MembershipRoles
         join membership in db.TenantMemberships on new { assignment.TenantId, assignment.MembershipId } equals new { membership.TenantId, MembershipId = membership.Id }
         where assignment.TenantId == tenantId && assignment.RoleId == ownerRoleId && membership.UserId == userId && membership.Status == MembershipStatus.Active
         select assignment.Id)
        .AnyAsync(cancellationToken);

    // Initialised by member, not by constructor: the database query can then be ordered and filtered by these members.
    private sealed record MemberRow
    {
        public Guid Id { get; init; }
        public required string Email { get; init; }
        public string? DisplayName { get; init; }
        public MembershipStatus Status { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private IQueryable<MemberRow> MemberRows(Guid tenantId) =>
        from membership in db.TenantMemberships.AsNoTracking()
        join user in db.Users on membership.UserId equals user.Id
        where membership.TenantId == tenantId
        select new MemberRow { Id = membership.Id, Email = user.Email, DisplayName = user.DisplayName, Status = membership.Status, CreatedAt = membership.CreatedAt };

    private async Task<List<MemberItem>> LoadAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        await ToItemsAsync(tenantId, await MemberRows(tenantId).Where(row => row.Id == membershipId).ToListAsync(cancellationToken), cancellationToken);

    /// <summary>The roles and products of just these members, in two small reads.</summary>
    private async Task<List<MemberItem>> ToItemsAsync(Guid tenantId, IReadOnlyList<MemberRow> members, CancellationToken cancellationToken)
    {
        var ids = members.Select(member => member.Id).ToList();

        var roles = (await (
                from assignment in db.MembershipRoles
                join role in db.Roles on new { assignment.TenantId, assignment.RoleId } equals new { role.TenantId, RoleId = role.Id }
                where assignment.TenantId == tenantId && ids.Contains(assignment.MembershipId)
                select new { assignment.MembershipId, role.Id, role.Name })
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .ToLookup(row => row.MembershipId, row => new MemberRoleDto(row.Id, row.Name));

        var applications = (await (
                from access in db.ApplicationAccess
                join application in db.Applications on access.ApplicationId equals application.Id
                where access.TenantId == tenantId && ids.Contains(access.MembershipId)
                select new { access.MembershipId, application.Id, application.Key })
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .ToLookup(row => row.MembershipId, row => new MemberApplicationDto(row.Id, row.Key));

        return [.. members.Select(member => new MemberItem(
            member.Id, member.Email, member.DisplayName, member.Status.ToString(),
            [.. roles[member.Id].OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase)],
            [.. applications[member.Id].OrderBy(application => application.ApplicationKey, StringComparer.Ordinal)],
            member.CreatedAt))];
    }
}
