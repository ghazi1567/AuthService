namespace AuthService.Api.Tokens;

/// <summary>The claims of a product token (TOK-01), besides the standard <c>sub</c>, <c>aud</c>, <c>iss</c> and times.</summary>
public static class ProductClaims
{
    /// <summary>The one tenant the token is for.</summary>
    public const string TenantId = "tid";

    /// <summary>The permissions the person holds in the token's product, as an array of <c>key:Level</c> strings.</summary>
    public const string Permissions = "permissions";
}
