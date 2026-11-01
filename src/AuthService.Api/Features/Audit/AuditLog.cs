using System.Text.RegularExpressions;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Audit;

/// <summary>
/// Records audit entries (AUD-01). Like the email outbox, it only adds the entry to the database context: the caller's own
/// <c>SaveChangesAsync</c> commits it in the same transaction as the action, so an entry exists if and only if the action
/// happened. An action that fails or rolls back leaves no entry.
/// </summary>
public sealed partial class AuditLog(AuthDbContext db, IHttpContextAccessor httpContextAccessor, TimeProvider timeProvider)
{
    /// <summary>
    /// Adds an entry. <paramref name="detail"/> is a short code (lowercase letters, digits, dots, underscores, hyphens): a rule that
    /// cannot be met by a password, token or other secret, so one cannot be recorded by mistake.
    /// </summary>
    public void Record(string action, AuditActor actor, Guid? tenantId = null, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (detail is not null && !DetailPattern().IsMatch(detail))
        {
            throw new ArgumentException(
                "An audit detail must be a short code: lowercase letters, digits, dots, underscores and hyphens, at most 128 characters.",
                nameof(detail));
        }

        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = timeProvider.GetUtcNow(),
            Action = action,
            ActorType = actor.Type,
            ActorId = actor.Id,
            TenantId = tenantId,
            SourceIp = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Detail = detail,
        });
    }

    [GeneratedRegex("^[a-z0-9_.-]{1,128}$")]
    private static partial Regex DetailPattern();
}
