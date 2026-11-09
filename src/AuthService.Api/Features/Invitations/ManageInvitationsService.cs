using AuthService.Api.Common;
using AuthService.Api.Email;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Roles;
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
}

/// <summary>
/// Lets a tenant admin see who has not yet accepted their invitation, and send the link again (a new one: the old link stops
/// working, and the new one lasts a full invitation period). Both only ever concern the caller's tenant.
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
}
