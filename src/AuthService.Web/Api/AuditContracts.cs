namespace AuthService.Web.Api;

/// <summary>One entry of the organisation's audit log. The API never records secrets, passwords or keys in any field.</summary>
public sealed record AuditEntryItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Action,
    string ActorType,
    Guid? ActorId,
    Guid? TenantId,
    string? SourceIp,
    string? Detail);

/// <summary>Where the next page starts: pass both values back as <c>before</c> and <c>beforeId</c>.</summary>
public sealed record AuditCursorItem(DateTimeOffset Before, Guid BeforeId);

/// <summary>A page of the audit log, newest first, and where the next one starts (null at the end).</summary>
public sealed record AuditPage(IReadOnlyList<AuditEntryItem> Items, AuditCursorItem? Next);

/// <summary>Plain words for the audit log's action codes.</summary>
public static class AuditLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["auth.signup.completed"] = "Signed up",
        ["auth.tenant.created"] = "Organisation created",
        ["auth.login.succeeded"] = "Signed in",
        ["auth.login.failed"] = "Sign-in failed",
        ["auth.account.locked"] = "Account paused after too many attempts",
        ["auth.email.verified"] = "Email address confirmed",
        ["auth.password_reset.completed"] = "Password reset",
        ["auth.refresh_token.reuse_detected"] = "Sign-in ended: a used sign-in token was presented again",
        ["auth.tenant.audit_read"] = "Viewed the audit log",
        ["auth.subscription.subscribed"] = "Product subscribed",
        ["auth.subscription.suspended"] = "Subscription suspended",
        ["auth.subscription.cancelled"] = "Subscription cancelled",
        ["auth.subscription.seat_limit_changed"] = "Seat limit changed",
        ["auth.role.created"] = "Role created",
        ["auth.role.updated"] = "Role changed",
        ["auth.role.deleted"] = "Role deleted",
        ["auth.apikey.created"] = "API key created",
        ["auth.apikey.rotated"] = "API key rotated",
        ["auth.apikey.revoked"] = "API key revoked",
        ["auth.membership.roles_changed"] = "Member's roles changed",
        ["auth.membership.disabled"] = "Member disabled",
        ["auth.membership.enabled"] = "Member enabled again",
        ["auth.membership.access_granted"] = "Product access given",
        ["auth.membership.access_revoked"] = "Product access taken away",
        ["auth.invitation.sent"] = "Invitation sent",
        ["auth.invitation.resent"] = "Invitation sent again",
        ["auth.invitation.accepted"] = "Invitation accepted",
    };

    /// <summary>The events an organisation's admin can have in their own log, by name, for the filter.</summary>
    public static IReadOnlyList<(string Code, string Label)> Choices { get; } =
        [.. Labels.Select(pair => (pair.Key, pair.Value)).OrderBy(choice => choice.Value, StringComparer.OrdinalIgnoreCase)];

    /// <summary>A readable name for an action code. A code with no name here reads as its last words, not as a raw code.</summary>
    public static string Describe(string code)
    {
        if (Labels.TryGetValue(code, out var label))
        {
            return label;
        }

        var words = code[(code.LastIndexOf('.') + 1)..].Replace('_', ' ').Trim();
        return words.Length == 0 ? "Something happened" : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
