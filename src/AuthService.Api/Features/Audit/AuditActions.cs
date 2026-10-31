namespace AuthService.Api.Features.Audit;

/// <summary>
/// The audited events, as stable codes. Add a code here before recording a new kind of event, so the list of what the audit
/// log can contain stays in one place.
/// </summary>
public static class AuditActions
{
    public const string SignupCompleted = "auth.signup.completed";
    public const string TenantCreated = "auth.tenant.created";

    public const string LoginSucceeded = "auth.login.succeeded";
    public const string LoginFailed = "auth.login.failed";
    public const string AccountLocked = "auth.account.locked";

    public const string EmailVerified = "auth.email.verified";
    public const string PasswordResetCompleted = "auth.password_reset.completed";

    /// <summary>A refresh token that had already been used was presented again, so its whole session family was revoked.</summary>
    public const string RefreshTokenReuseDetected = "auth.refresh_token.reuse_detected";

    /// <summary>A platform admin read the audit log. Reading the log is itself an admin action worth tracing.</summary>
    public const string AdminAuditRead = "auth.admin.audit_read";

    public const string SigningKeyCreated = "auth.signing_key.created";
    public const string SigningKeyRotated = "auth.signing_key.rotated";
}
