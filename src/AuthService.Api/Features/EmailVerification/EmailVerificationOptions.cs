namespace AuthService.Api.Features.EmailVerification;

/// <summary>Settings for email verification, bound from the <c>Auth:EmailVerification</c> configuration section.</summary>
public sealed class EmailVerificationOptions
{
    public const string SectionName = "Auth:EmailVerification";

    /// <summary>How long a verification link works. 24 hours (TEN-11).</summary>
    public int TokenHours { get; set; } = 24;

    /// <summary>
    /// A new link is not sent for an account that was sent one less than this many seconds ago. Protects the address's
    /// owner from being flooded with email by repeated requests.
    /// </summary>
    public int ResendCooldownSeconds { get; set; } = 60;

    /// <summary>How many resend requests one IP address may make per window before it gets 429.</summary>
    public int ResendIpLimit { get; set; } = 10;

    public int ResendIpWindowMinutes { get; set; } = 10;
}
