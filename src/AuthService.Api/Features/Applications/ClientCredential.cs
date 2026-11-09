namespace AuthService.Api.Features.Applications;

/// <summary>
/// The one string a product team is given (ONB-03): its client id and its client secret joined by a dot, such as
/// <c>cl_AbC123.the-secret</c>. A client id and a secret never contain a dot (both are base64url), so the first dot is the join. The
/// token endpoint accepts it as the client secret on its own, or the old pair, so nothing that already works stops working.
/// </summary>
public static class ClientCredential
{
    public const char Separator = '.';

    private const string ClientIdPrefix = "cl_";

    public static string Combine(string clientId, string clientSecret) => $"{clientId}{Separator}{clientSecret}";

    /// <summary>Splits a combined credential. False for anything that is not one, such as a plain secret.</summary>
    public static bool TryParse(string? credential, out string clientId, out string clientSecret)
    {
        clientId = "";
        clientSecret = "";
        if (string.IsNullOrEmpty(credential))
        {
            return false;
        }

        var dot = credential.IndexOf(Separator);
        if (dot <= ClientIdPrefix.Length || dot == credential.Length - 1 || !credential.StartsWith(ClientIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        clientId = credential[..dot];
        clientSecret = credential[(dot + 1)..];
        return true;
    }
}
