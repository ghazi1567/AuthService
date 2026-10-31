namespace AuthService.Api.Features.Login;

/// <summary>Settings that protect login against password guessing (TEN-12), bound from <c>Auth:LoginProtection</c>.</summary>
public sealed class LoginProtectionOptions
{
    public const string SectionName = "Auth:LoginProtection";

    /// <summary>Wrong passwords in a row after which the account is locked. 5 (TEN-12).</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long a locked account stays locked. 15 minutes (TEN-12).</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>How many login requests one IP address may make per window before it gets 429, across all accounts.</summary>
    public int IpLimit { get; set; } = 20;

    public int IpWindowMinutes { get; set; } = 1;
}
