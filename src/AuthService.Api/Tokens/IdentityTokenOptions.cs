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
    /// The secret that encrypts signing keys before they are stored in the database: 32 random bytes, base64 encoded
    /// (<c>openssl rand -base64 32</c>). Required outside Development and Testing, where a fixed, publicly known key is used
    /// instead. Set it through a secret, never a committed file (environment variable
    /// <c>Auth__Tokens__KeyEncryptionKey</c>). Losing it means losing every stored signing key.
    /// </summary>
    public string? KeyEncryptionKey { get; set; }

    /// <summary>How long one signing key signs before the next takes over. 90 days (TOK-02).</summary>
    public int SigningKeyRotationDays { get; set; } = 90;

    /// <summary>A new key is published this long before it starts signing, so every product knows it first. 24 hours.</summary>
    public int SigningKeyPrePublishHours { get; set; } = 24;

    /// <summary>A key stays published this long after it stops signing, so tokens already issued still validate. 24 hours.</summary>
    public int SigningKeyPostRetireHours { get; set; } = 24;

    /// <summary>How often each instance reloads the keys from the database and checks whether it is time to rotate.</summary>
    public int SigningKeyRefreshSeconds { get; set; } = 60;
}
