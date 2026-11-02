namespace AuthService.Web.Api;

/// <summary>Where the Auth Service API is, bound from <c>AuthService:Api</c>. The web app is one more client of that API.</summary>
public sealed class AuthApiOptions
{
    public const string SectionName = "AuthService:Api";

    /// <summary>Base address of the API, for example <c>http://localhost:5080</c>. Required: the app refuses to start without it.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>How long a call to the API may take before it is treated as unreachable.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Reasons the settings are unusable, so a typo stops the deployment instead of failing the first page.</summary>
    public IEnumerable<string> Problems()
    {
        if (BaseUrl is null)
        {
            yield return $"{SectionName}:BaseUrl is required: the address of the Auth Service API.";
        }
        else if (!BaseUrl.IsAbsoluteUri || (BaseUrl.Scheme != Uri.UriSchemeHttp && BaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            yield return $"{SectionName}:BaseUrl must be an absolute http or https address.";
        }

        if (TimeoutSeconds is < 1 or > 120)
        {
            yield return $"{SectionName}:TimeoutSeconds must be between 1 and 120.";
        }
    }
}
