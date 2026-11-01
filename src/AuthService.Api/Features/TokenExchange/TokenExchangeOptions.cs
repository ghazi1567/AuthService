namespace AuthService.Api.Features.TokenExchange;

/// <summary>Settings for token exchange, bound from <c>Auth:TokenExchange</c>.</summary>
public sealed class TokenExchangeOptions
{
    public const string SectionName = "Auth:TokenExchange";

    /// <summary>
    /// How many exchanges one signed-in person may make per window before they get 429. Each exchange reads the person's membership,
    /// the tenant's subscription and every role they hold, and a product token lasts 15 minutes, so a person needs a handful an hour:
    /// the default is generous and only stops a client that is looping.
    /// </summary>
    public int UserLimit { get; set; } = 30;

    public int UserWindowMinutes { get; set; } = 1;
}
