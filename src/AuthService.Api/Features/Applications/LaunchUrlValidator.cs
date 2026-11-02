using System.Net;

namespace AuthService.Api.Features.Applications;

/// <summary>
/// Checks the address a product is opened at from the console (APP-08). It is only ever followed by a person's own browser, so unlike a
/// webhook address it is not checked for private network addresses; it is kept to shapes that cannot hide anything: absolute, https
/// (plain http only for this machine, so development works), no user name or password, no fragment.
/// </summary>
public static class LaunchUrlValidator
{
    public const int MaxLength = 2000;

    public static bool TryValidate(string text, out string address, out string error)
    {
        address = "";
        var trimmed = text.Trim();
        if (trimmed.Length > MaxLength)
        {
            error = $"Url must be at most {MaxLength} characters.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Url must be a full web address starting with https://.";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsThisMachine(uri))
        {
            error = "Url must use https. Plain http is only accepted for localhost.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Url must not contain a user name or password.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Url must not contain a # fragment.";
            return false;
        }

        address = uri.AbsoluteUri;
        error = "";
        return true;
    }

    private static bool IsThisMachine(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));
}
