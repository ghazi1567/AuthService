namespace AuthService.Api.Features.PasswordReset;

/// <summary>Settings for password reset, bound from the <c>Auth:PasswordReset</c> configuration section.</summary>
public sealed class PasswordResetOptions
{
    public const string SectionName = "Auth:PasswordReset";

    /// <summary>How long a reset link works. 60 minutes (TEN-10).</summary>
    public int TokenMinutes { get; set; } = 60;

    /// <summary>
    /// An account that was sent a reset link less than this many seconds ago is not sent another. Protects the address's
    /// owner from being flooded with email by repeated requests.
    /// </summary>
    public int RequestCooldownSeconds { get; set; } = 60;

    /// <summary>How many requests one IP address may make per window before it gets 429.</summary>
    public int IpLimit { get; set; } = 10;

    public int IpWindowMinutes { get; set; } = 10;
}
