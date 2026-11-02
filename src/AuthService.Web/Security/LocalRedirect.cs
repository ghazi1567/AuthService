namespace AuthService.Web.Security;

/// <summary>Where to send someone after they sign in. Only a path on this site is allowed, so a crafted link cannot send them elsewhere.</summary>
public static class LocalRedirect
{
    public const string Home = "/";

    /// <summary>The address if it is a path on this site, otherwise the home page.</summary>
    public static string Safe(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/')
        {
            return Home;
        }

        // "//host" and "/\host" are read by browsers as another site; control characters can split a header.
        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
        {
            return Home;
        }

        return returnUrl.Any(char.IsControl) ? Home : returnUrl;
    }
}
