namespace AuthService.Api.Tokens;

/// <summary>Settings for identity tokens, bound from the <c>Auth:Tokens</c> configuration section.</summary>
public sealed class IdentityTokenOptions
{
    public const string SectionName = "Auth:Tokens";

    public string Issuer { get; set; } = "AuthService";

    /// <summary>How long an identity access token is valid. 15 minutes by default.</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>How long a refresh token is valid. 30 days (decision Q-09).</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// EC (P-256) private key in PEM form used to sign tokens. Required outside Development and Testing, where a
    /// throwaway key is generated at startup instead. Set it through a secret, never a committed file
    /// (environment variable <c>Auth__Tokens__SigningKeyPem</c>).
    /// </summary>
    public string? SigningKeyPem { get; set; }
}
