namespace AuthService.Authorization;

/// <summary>What a product API tells the package about itself and where the Auth Service publishes its keys.</summary>
public sealed class AuthServiceAuthorizationOptions
{
    /// <summary>
    /// The product's own audience, exactly as registered with the Auth Service (<c>JwtAudience</c>). A token for any other audience is
    /// refused, even with a valid signature, so a token issued for one product cannot be replayed against another. Required.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>The Auth Service's token issuer (<c>iss</c>). Required.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Where the Auth Service publishes its public keys, such as <c>https://auth.example.com/.well-known/jwks.json</c>. Required.</summary>
    public Uri? JwksUrl { get; set; }

    /// <summary>
    /// Allows a JWKS address that is plain <c>http</c>. Off by default, since keys fetched over an unprotected connection could be
    /// swapped. For local development only.
    /// </summary>
    public bool AllowInsecureJwksUrl { get; set; }

    /// <summary>How long the keys are kept before they are fetched again. 10 minutes: new keys are published a day before they sign.</summary>
    public int KeyCacheMinutes { get; set; } = 10;

    /// <summary>
    /// The shortest time between two fetches forced by a token signed with a key the product does not have yet, so a stream of
    /// forged key ids cannot make the product hammer the Auth Service. 30 seconds.
    /// </summary>
    public int MinimumRefreshSeconds { get; set; } = 30;

    /// <summary>How much clock difference between machines is forgiven when checking a token's times. 30 seconds.</summary>
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>The name of the <see cref="System.Net.Http.IHttpClientFactory"/> client used to fetch the keys. Replace its handler in tests.</summary>
    public const string JwksHttpClientName = "AuthService.Jwks";
}
