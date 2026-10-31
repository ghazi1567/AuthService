namespace AuthService.Api.Features.Applications;

/// <summary>Settings for a product's client credentials (APP-05), bound from <c>Auth:Applications</c>.</summary>
public sealed class ApplicationCredentialOptions
{
    public const string SectionName = "Auth:Applications";

    /// <summary>How long a client secret keeps working after it is rotated out, unless the rotation asks for another time. 24 hours.</summary>
    public int SecretGraceHours { get; set; } = 24;

    /// <summary>How many token requests one IP address may make per window before it gets 429, across all products.</summary>
    public int TokenIpLimit { get; set; } = 60;

    public int TokenIpWindowMinutes { get; set; } = 1;
}
