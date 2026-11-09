using AuthService.Api.Common;
using AuthService.Api.Email;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Invitations;

/// <summary>An invitation nobody has accepted yet, as a tenant admin sees it. The link itself is only ever in the email.</summary>
public sealed record PendingInvitation(
    Guid MembershipId,
    string Email,
    IReadOnlyList<AssignedRoleDto> Roles,
    IReadOnlyList<Guid> ApplicationIds,
    string InvitedBy,
    DateTimeOffset InvitedAt,
    DateTimeOffset LastSentAt,
    DateTimeOffset ExpiresAt,
    bool IsExpired);

public abstract record ResendInvitationResult
{
    public sealed record Resent(InvitationResponse Invitation) : ResendInvitationResult;

    /// <summary>No such membership in the caller's tenant.</summary>
    public sealed record NotFound : ResendInvitationResult;

    /// <summary>The person has already accepted, or the membership was never an open invitation.</summary>
    public sealed record NotPending : ResendInvitationResult;

    /// <summary>The link was emailed moments ago.</summary>
    public sealed record TooSoon(int RetryAfterSeconds) : ResendInvitationResult;

    /// <summary>
    /// The invitation had expired, which freed its seats, and a product it gives access to has since filled up. Sending it again would
    /// take a seat that is not there. The keys name the full products.
    /// </summary>
    public sealed record NoSeatAvailable(IReadOnlyList<string> ApplicationKeys) : ResendInvitationResult;
}

public abstract record RevokeInvitationResult
{
    public sealed record Revoked : RevokeInvitationResult;

    /// <summary>No such membership in the caller's tenant.</summary>
    public sealed record NotFound : RevokeInvitationResult;

    /// <summary>The person has already accepted, or the membership was never an open invitation.</summary>
    public sealed record NotPending : RevokeInvitationResult;
}

/// <summary>
/// Lets a tenant admin see who has not yet accepted their invitation, send the link again (a new one: the old link stops working, and
/// the new one lasts a full invitation period), or withdraw it. All of it only ever concerns the caller's tenant.
/// </summary>
public sealed class ManageInvitationsService(
    AuthDbContext db,
    EmailOutbox outbox,
    IOptions<InvitationOptions> options,
    AuditLog audit,
    TimeProvider timeProvider)
{
    // Initialised by member, not by constructor: the database query can then be ordered and filtered by these members.
    private sealed record PendingRow
    {
        public Guid InvitationId { get; init; }
        public Guid MembershipId { get; init; }
        public required string Email { get; init; }
        public required string InvitedBy { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastSentAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    /// <summary>One page of the invitations still waiting to be accepted, newest first (PLT-08).</summary>
    public async Task<PagedResponse<PendingInvitation, TimeCursor>> ListPendingAsync(Guid tenantId, PageQuery page, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var rows = await page.NewestFirstAsync(
            from invitation in db.Invitations.AsNoTracking()
            join membership in db.TenantMemberships on new { invitation.TenantId, invitation.MembershipId } equals new { membership.TenantId, MembershipId = membership.Id }
            join invitee in db.Users on membership.UserId equals invitee.Id
            join inviter in db.Users on invitation.InvitedByUserId equals inviter.Id
            where invitation.TenantId == tenantId && invitation.UsedAt == null && membership.Status == MembershipStatus.Invited
            select new PendingRow
            {
                InvitationId = invitation.Id,
                MembershipId = invitation.MembershipId,
                Email = invitee.Email,
                InvitedBy = inviter.Email,
                CreatedAt = invitation.CreatedAt,
                LastSentAt = invitation.LastSentAt,
                ExpiresAt = invitation.ExpiresAt,
            },
            row => row.CreatedAt,
            row => row.InvitationId,
            cancellationToken);
        var open = rows.Rows;
        if (open.Count == 0)
        {
            return new PagedResponse<PendingInvitation, TimeCursor>([], rows.Next);
        }

        var membershipIds = open.Select(item => item.MembershipId).ToList();
        var roles = await (
            from assignment in db.MembershipRoles
            join role in db.Roles on new { assignment.TenantId, assignment.RoleId } equals new { role.TenantId, RoleId = role.Id }
            where assignment.TenantId == tenantId && membershipIds.Contains(assignment.MembershipId)
            select new { assignment.MembershipId, Role = new AssignedRoleDto(role.Id, role.Name, role.IsBuiltIn) })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var access = await db.ApplicationAccess.AsNoTracking()
            .Where(grant => grant.TenantId == tenantId && membershipIds.Contains(grant.MembershipId))
            .Select(grant => new { grant.MembershipId, grant.ApplicationId })
            .ToListAsync(cancellationToken);

        return new PagedResponse<PendingInvitation, TimeCursor>([.. open.Select(item => new PendingInvitation(
            item.MembershipId,
            item.Email,
            [.. roles.Where(entry => entry.MembershipId == item.MembershipId).Select(entry => entry.Role).OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase)],
            [.. access.Where(entry => entry.MembershipId == item.MembershipId).Select(entry => entry.ApplicationId).Order()],
            item.InvitedBy,
            item.CreatedAt,
            item.LastSentAt,
            item.ExpiresAt,
            item.ExpiresAt <= now))], rows.Next);
    }

    public async Task<ResendInvitationResult> ResendAsync(Guid callerUserId, Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var membership = await db.TenantMemberships
            .FromSqlInterpolated($"SELECT * FROM tenant_memberships WHERE id = {membershipId} AND tenant_id = {tenantId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (membership is null)
        {
            return new ResendInvitationResult.NotFound();
        }

        var invitation = membership.Status == MembershipStatus.Invited
            ? await db.Invitations
                .Where(candidate => candidate.TenantId == tenantId && candidate.MembershipId == membershipId && candidate.UsedAt == null)
                .OrderByDescending(candidate => candidate.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        if (invitation is null)
        {
            return new ResendInvitationResult.NotPending();
        }

        var now = timeProvider.GetUtcNow();
        var waitUntil = invitation.LastSentAt.AddSeconds(options.Value.ResendCooldownSeconds);
        if (now < waitUntil)
        {
            return new ResendInvitationResult.TooSoon((int)Math.Ceiling((waitUntil - now).TotalSeconds));
        }

        // An invitation past its lifetime holds no seat (see SeatHolders), so sending it again takes the seats back, and that needs the same
        // check and the same row locks as a new invitation. A live invitation already holds its seats, so there is nothing to check.
        if (invitation.ExpiresAt <= now)
        {
            var full = await FullProductsAsync(tenantId, membershipId, now, cancellationToken);
            if (full.Count > 0)
            {
                return new ResendInvitationResult.NoSeatAvailable(full);
            }
        }

        // The same row gets a new token and a new expiry, so the old link no longer matches anything.
        var token = SecretToken.NewValue();
        invitation.TokenHash = SecretToken.Hash(token);
        invitation.ExpiresAt = now.AddDays(options.Value.TokenDays);
        invitation.LastSentAt = now;

        var email = await db.Users.AsNoTracking().Where(user => user.Id == membership.UserId).Select(user => user.Email).SingleAsync(cancellationToken);
        var tenantName = await db.Tenants.AsNoTracking().Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.Name).SingleAsync(cancellationToken);

        // Queued in the same save, so the email exists only if the new link does.
        outbox.Enqueue(EmailTemplate.Invitation, email, new Dictionary<string, string> { ["token"] = token, ["tenantName"] = tenantName, [EmailLifetime.DataKey] = EmailLifetime.Describe(TimeSpan.FromDays(options.Value.TokenDays)) });
        audit.Record(AuditActions.InvitationResent, AuditActor.User(callerUserId), tenantId, membershipId.ToString());

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var roles = await (
            from assignment in db.MembershipRoles
            join role in db.Roles on new { assignment.TenantId, assignment.RoleId } equals new { role.TenantId, RoleId = role.Id }
            where assignment.TenantId == tenantId && assignment.MembershipId == membershipId
            select new AssignedRoleDto(role.Id, role.Name, role.IsBuiltIn))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var applicationIds = await db.ApplicationAccess.AsNoTracking()
            .Where(grant => grant.TenantId == tenantId && grant.MembershipId == membershipId)
            .Select(grant => grant.ApplicationId)
            .ToListAsync(cancellationToken);

        return new ResendInvitationResult.Resent(new InvitationResponse(
            membershipId,
            email,
            membership.Status.ToString(),
            [.. roles.OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase)],
            [.. applicationIds.Order()],
            invitation.ExpiresAt));
    }

    /// <summary>
    /// Withdraws an invitation nobody has accepted: the link stops working at once, the person's seats are freed, and the pending membership
    /// is removed, so the same address can be invited again (it would otherwise be refused as "already a member"). Anyone who has accepted
    /// is a member, and leaves through the member list, not here.
    /// </summary>
    public async Task<RevokeInvitationResult> RevokeAsync(Guid callerUserId, Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The same row lock as resending and accepting, so a revoke and an acceptance at the same moment cannot both win.
        var membership = await db.TenantMemberships
            .FromSqlInterpolated($"SELECT * FROM tenant_memberships WHERE id = {membershipId} AND tenant_id = {tenantId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (membership is null)
        {
            return new RevokeInvitationResult.NotFound();
        }

        // Pending means Invited with a link that was never used. Expired counts: a stale invitation is exactly what is being cleaned up.
        var pending = membership.Status == MembershipStatus.Invited
            && await db.Invitations.AnyAsync(
                candidate => candidate.TenantId == tenantId && candidate.MembershipId == membershipId && candidate.UsedAt == null, cancellationToken);
        if (!pending)
        {
            return new RevokeInvitationResult.NotPending();
        }

        // Children first (the foreign keys restrict), then the membership itself.
        await db.Invitations.Where(candidate => candidate.MembershipId == membershipId).ExecuteDeleteAsync(cancellationToken);
        await db.MembershipRoles.Where(assignment => assignment.MembershipId == membershipId).ExecuteDeleteAsync(cancellationToken);
        await db.ApplicationAccess.Where(access => access.MembershipId == membershipId).ExecuteDeleteAsync(cancellationToken);
        await db.TenantMemberships.Where(candidate => candidate.Id == membershipId).ExecuteDeleteAsync(cancellationToken);

        // A person who exists only because of this invitation (no password, no other organisation) is not kept as an account nobody can
        // sign in to; anyone who has an account or belongs somewhere else is left exactly as they are.
        await db.Users
            .Where(user => user.Id == membership.UserId && user.PasswordHash == null
                && !db.TenantMemberships.Any(other => other.UserId == user.Id))
            .ExecuteDeleteAsync(cancellationToken);

        audit.Record(AuditActions.InvitationRevoked, AuditActor.User(callerUserId), tenantId, membershipId.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RevokeInvitationResult.Revoked();
    }

    /// <summary>
    /// The keys of the products that cannot take this membership's seat back, checked under the same row locks as inviting. The membership
    /// is not counted yet (its invitation has expired), so a product whose count is already at the limit is full.
    /// </summary>
    private async Task<IReadOnlyList<string>> FullProductsAsync(Guid tenantId, Guid membershipId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var applicationIds = await db.ApplicationAccess.AsNoTracking()
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

            if (await db.SeatsUsedAsync(tenantId, applicationId, now, cancellationToken) >= maxSeats)
            {
                full.Add(await db.Applications.AsNoTracking().Where(application => application.Id == applicationId)
                    .Select(application => application.Key).SingleAsync(cancellationToken));
            }
        }

        return [.. full.Order(StringComparer.Ordinal)];
    }
}
