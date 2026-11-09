using AuthService.Api.Common;
using AuthService.Api.Email;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Invitations;

/// <summary>Body of <c>POST /api/auth/tenants/me/invitations</c>.</summary>
public sealed record InviteRequest(string? Email, IReadOnlyList<Guid>? RoleIds, IReadOnlyList<Guid>? ApplicationIds);

/// <summary>
/// The invitation as the inviter sees it. It looks the same whether the email was new to the platform or already had an account,
/// so inviting reveals nothing about other tenants. The link is only in the email.
/// </summary>
public sealed record InvitationResponse(
    Guid MembershipId,
    string Email,
    string Status,
    IReadOnlyList<AssignedRoleDto> Roles,
    IReadOnlyList<Guid> ApplicationIds,
    DateTimeOffset ExpiresAt);

public sealed record ValidInvite(string Email, IReadOnlyList<Guid> RoleIds, IReadOnlyList<Guid> ApplicationIds);

public abstract record InviteResult
{
    public sealed record Invited(InvitationResponse Invitation) : InviteResult;

    /// <summary>The email already has a membership in this tenant, in any status.</summary>
    public sealed record AlreadyMember : InviteResult;

    /// <summary>A role is not in the caller's tenant, or does not exist.</summary>
    public sealed record RoleNotFound : InviteResult;

    public sealed record OwnerRoleRestricted : InviteResult;

    /// <summary>A product is not one the tenant is actively subscribed to.</summary>
    public sealed record ApplicationNotSubscribed : InviteResult;

    public sealed record NoSeatAvailable(IReadOnlyList<string> ApplicationKeys) : InviteResult;
}

public static class InviteValidator
{
    public const int MaxApplications = 20;

    public static bool TryValidate(InviteRequest request, out ValidInvite? valid, out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = null;

        var email = request.Email is null ? null : EmailNormalizer.Normalize(request.Email);
        if (string.IsNullOrEmpty(email))
        {
            errors[nameof(InviteRequest.Email)] = ["Email is required."];
        }
        else if (!SignupValidator.IsPlausibleEmail(email))
        {
            errors[nameof(InviteRequest.Email)] = ["Email is not a valid address."];
        }

        var roleIds = request.RoleIds?.Distinct().ToList() ?? [];
        if (roleIds.Count is < 1 or > AssignRolesValidator.MaxRolesPerMember || roleIds.Contains(Guid.Empty))
        {
            errors[nameof(InviteRequest.RoleIds)] = [$"Give between 1 and {AssignRolesValidator.MaxRolesPerMember} roles."];
        }

        var applicationIds = request.ApplicationIds?.Distinct().ToList() ?? [];
        if (applicationIds.Count > MaxApplications || applicationIds.Contains(Guid.Empty))
        {
            errors[nameof(InviteRequest.ApplicationIds)] = [$"Give at most {MaxApplications} valid application ids, or none."];
        }

        if (errors.Count > 0)
        {
            return false;
        }

        valid = new ValidInvite(email!, roleIds, applicationIds);
        return true;
    }
}

/// <summary>
/// TEN-08: invites a person into the caller's tenant. It creates an Invited membership holding the chosen roles, optionally gives
/// it access to products (an Invited member with access takes a seat), and emails a single-use link that expires after 7 days.
/// A person who is new to the platform gets a user without a password; the link lets them choose one.
/// </summary>
public sealed class InviteUserService(
    AuthDbContext db,
    EmailOutbox outbox,
    IOptions<InvitationOptions> options,
    AuditLog audit,
    TimeProvider timeProvider)
{
    public async Task<InviteResult> InviteAsync(Guid callerUserId, Guid tenantId, ValidInvite input, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Two invitations of one email to one tenant take turns, so the second sees the first and is told it is already a member.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"invite:" + tenantId + ":" + input.Email}, 0))", cancellationToken);

        var roles = await db.Roles.AsNoTracking()
            .Where(role => role.TenantId == tenantId && input.RoleIds.Contains(role.Id))
            .Select(role => new { role.Id, role.Name, role.IsBuiltIn })
            .ToListAsync(cancellationToken);
        if (roles.Count != input.RoleIds.Count)
        {
            return new InviteResult.RoleNotFound();
        }

        // Giving the Owner role is reserved to Owners, or anyone allowed to invite could invite themselves a second account as one.
        if (roles.Any(role => role.IsBuiltIn) && !await IsActiveOwnerAsync(tenantId, callerUserId, cancellationToken))
        {
            return new InviteResult.OwnerRoleRestricted();
        }

        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Email == input.Email, cancellationToken);
        if (user is not null
            && await db.TenantMemberships.AnyAsync(membership => membership.TenantId == tenantId && membership.UserId == user.Id, cancellationToken))
        {
            return new InviteResult.AlreadyMember();
        }

        var now = timeProvider.GetUtcNow();

        // Seats: the same check and the same lock as granting access directly. An Invited member with access takes a seat.
        var full = new List<string>();
        foreach (var applicationId in input.ApplicationIds.Order())
        {
            var subscription = await db.Subscriptions
                .FromSqlInterpolated($"SELECT * FROM subscriptions WHERE tenant_id = {tenantId} AND application_id = {applicationId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            if (subscription is not { Status: SubscriptionStatus.Active })
            {
                return new InviteResult.ApplicationNotSubscribed();
            }

            if (subscription.MaxSeats is { } maxSeats && await db.SeatsUsedAsync(tenantId, applicationId, cancellationToken) >= maxSeats)
            {
                full.Add(await db.Applications.AsNoTracking().Where(application => application.Id == applicationId)
                    .Select(application => application.Key).SingleAsync(cancellationToken));
            }
        }

        if (full.Count > 0)
        {
            return new InviteResult.NoSeatAvailable([.. full.Order(StringComparer.Ordinal)]);
        }

        if (user is null)
        {
            // A person new to the platform: no password yet, and no verified email until they accept.
            user = new User { Id = Guid.CreateVersion7(), Email = input.Email, CreatedAt = now };
            db.Users.Add(user);
        }

        var membership = new TenantMembership
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = user.Id,
            Status = MembershipStatus.Invited,
            CreatedAt = now,
        };
        db.TenantMemberships.Add(membership);

        foreach (var role in roles)
        {
            db.MembershipRoles.Add(new MembershipRole
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                MembershipId = membership.Id,
                RoleId = role.Id,
                AssignedAt = now,
            });
        }

        foreach (var applicationId in input.ApplicationIds)
        {
            db.ApplicationAccess.Add(new MembershipApplicationAccess
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                MembershipId = membership.Id,
                ApplicationId = applicationId,
                GrantedAt = now,
            });
        }

        var token = SecretToken.NewValue();
        var expiresAt = now.AddDays(options.Value.TokenDays);
        db.Invitations.Add(new Invitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            MembershipId = membership.Id,
            InvitedByUserId = callerUserId,
            TokenHash = SecretToken.Hash(token),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            LastSentAt = now,
        });

        var tenantName = await db.Tenants.AsNoTracking().Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.Name).SingleAsync(cancellationToken);

        // Queued in the same save, so the email exists only if the invitation does.
        outbox.Enqueue(EmailTemplate.Invitation, user.Email, new Dictionary<string, string> { ["token"] = token, ["tenantName"] = tenantName, [EmailLifetime.DataKey] = EmailLifetime.Describe(TimeSpan.FromDays(options.Value.TokenDays)) });
        audit.Record(AuditActions.InvitationSent, AuditActor.User(callerUserId), tenantId, membership.Id.ToString());

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new InviteResult.Invited(new InvitationResponse(
            membership.Id,
            user.Email,
            membership.Status.ToString(),
            [.. roles.Select(role => new AssignedRoleDto(role.Id, role.Name, role.IsBuiltIn)).OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase)],
            [.. input.ApplicationIds],
            expiresAt));
    }

    private async Task<bool> IsActiveOwnerAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await (from assignment in db.MembershipRoles
               join role in db.Roles on new { assignment.TenantId, assignment.RoleId } equals new { role.TenantId, RoleId = role.Id }
               join membership in db.TenantMemberships on new { assignment.TenantId, assignment.MembershipId } equals new { membership.TenantId, MembershipId = membership.Id }
               where assignment.TenantId == tenantId && role.IsBuiltIn && role.Name == Role.OwnerName
                   && membership.UserId == userId && membership.Status == MembershipStatus.Active
               select assignment.Id)
            .AnyAsync(cancellationToken);
}
