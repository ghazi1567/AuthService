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

    /// <summary>A tenant admin read their tenant's audit log (AUD-01). The actor is the admin and the tenant is theirs.</summary>
    public const string TenantAuditRead = "auth.tenant.audit_read";

    /// <summary>A platform admin registered a product. The detail is the product's key.</summary>
    public const string ApplicationRegistered = "auth.application.registered";

    /// <summary>A platform admin replaced a product's client secret. The detail is the product's key; no secret is ever recorded.</summary>
    public const string ApplicationSecretRotated = "auth.application.secret_rotated";

    /// <summary>A platform admin registered or updated a product's permission catalog. The detail is the product's key.</summary>
    public const string PermissionsRegistered = "auth.application.permissions_registered";

    /// <summary>A platform admin made a product available to subscribe to, or withdrew it. The detail is the product's key.</summary>
    public const string ApplicationActivated = "auth.application.activated";
    public const string ApplicationDeactivated = "auth.application.deactivated";

    /// <summary>A product subscribed a tenant to itself (SUB-01). The actor is the product, the tenant is the subscribed tenant, the detail is the product's key.</summary>
    public const string TenantSubscribed = "auth.subscription.subscribed";

    /// <summary>A tenant admin created a role (ROL-01). The actor is the admin, the tenant is theirs, the detail is the role id.</summary>
    public const string RoleCreated = "auth.role.created";

    /// <summary>A tenant admin changed or deleted a role (ROL-03, ROL-04). The actor is the admin, the tenant is theirs, the detail is the role id.</summary>
    public const string RoleUpdated = "auth.role.updated";
    public const string RoleDeleted = "auth.role.deleted";

    /// <summary>A tenant admin replaced the roles a member holds (ROL-05). The actor is the admin, the tenant is theirs, the detail is the membership id.</summary>
    public const string MemberRolesChanged = "auth.membership.roles_changed";

    /// <summary>A tenant admin disabled or re-enabled a member's membership (TEN-07). The actor is the admin, the tenant is theirs, the detail is the membership id.</summary>
    public const string MemberDisabled = "auth.membership.disabled";
    public const string MemberEnabled = "auth.membership.enabled";

    /// <summary>A tenant admin invited a person, and the person accepted (TEN-08). The tenant is the inviting one, the detail is the membership id.</summary>
    public const string InvitationSent = "auth.invitation.sent";
    public const string InvitationAccepted = "auth.invitation.accepted";

    /// <summary>A tenant admin gave a member access to a product, or took it away (SUB-05). The actor is the admin, the tenant is theirs, the detail is <c>membership-id.product-key</c>.</summary>
    public const string MemberAccessGranted = "auth.membership.access_granted";
    public const string MemberAccessRevoked = "auth.membership.access_revoked";

    /// <summary>A product suspended or cancelled a tenant's subscription (SUB-02). The actor is the product, the tenant is the one concerned, the detail is the product's key.</summary>
    public const string SubscriptionSuspended = "auth.subscription.suspended";
    public const string SubscriptionCancelled = "auth.subscription.cancelled";

    /// <summary>A product changed a tenant's seat limit (SUB-04). The actor is the product, the tenant is the one concerned, the detail is the product's key.</summary>
    public const string SeatLimitChanged = "auth.subscription.seat_limit_changed";

    public const string SigningKeyCreated = "auth.signing_key.created";
    public const string SigningKeyRotated = "auth.signing_key.rotated";
}
