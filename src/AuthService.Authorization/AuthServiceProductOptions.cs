namespace AuthService.Authorization;

/// <summary>
/// What a product tells the package when it is set up from the Auth Service's own address (<c>AddAuthServiceProduct</c>): where the Auth
/// Service is and the client secret it gave the product. Everything else (the issuer, the product's audience, where the keys are) is
/// learned from the Auth Service itself. The property names match the settings
/// <c>AuthService:Url</c> and <c>AuthService:ClientSecret</c> (<c>AuthService__Url</c> and <c>AuthService__ClientSecret</c> as environment
/// variables), so <c>builder.Configuration.GetSection("AuthService").Bind(options)</c> is enough.
/// </summary>
public sealed class AuthServiceProductOptions
{
    /// <summary>The name of the <see cref="System.Net.Http.IHttpClientFactory"/> client used to talk to the Auth Service. Replace its handler in tests.</summary>
    public const string HttpClientName = "AuthService.Product";

    /// <summary>The address of the Auth Service's API, such as <c>https://auth.example.com</c>. Required.</summary>
    public Uri? Url { get; set; }

    /// <summary>
    /// The client secret the platform admin gave the product: the one string with the client id in it, such as <c>cl_AbC123.the-secret</c>.
    /// Keep it in the host's secret store, never in the repository or the front end. Required.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// The client id, only when the product was given a client id and a secret separately (the older form). Leave it empty with the
    /// one string above.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Allows an address that is plain <c>http</c>. Off by default: the client secret and the keys must not travel unprotected. For local
    /// development only.
    /// </summary>
    public bool AllowInsecureUrl { get; set; }

    /// <summary>How long what was learned (the issuer and the product's audience) is kept before it is read again. 60 minutes; neither changes in practice.</summary>
    public int ConfigurationCacheMinutes { get; set; } = 60;

    /// <summary>How long the keys are kept before they are fetched again. 10 minutes: new keys are published a day before they sign.</summary>
    public int KeyCacheMinutes { get; set; } = 10;

    /// <summary>
    /// The shortest time between two attempts after the Auth Service could not be reached, and between two fetches forced by a token signed
    /// with a key the product does not have yet. 30 seconds, so a flood of tokens never makes the product hammer the Auth Service.
    /// </summary>
    public int MinimumRefreshSeconds { get; set; } = 30;

    /// <summary>How much clock difference between machines is forgiven when checking a token's times. 30 seconds.</summary>
    public int ClockSkewSeconds { get; set; } = 30;
}
