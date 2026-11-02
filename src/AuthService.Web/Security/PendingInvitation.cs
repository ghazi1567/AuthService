using Microsoft.AspNetCore.DataProtection;

namespace AuthService.Web.Security;

/// <summary>
/// Holds an invitation link while its owner signs in. An invitation for an email that already has an account can only be accepted
/// signed in, so the person goes to the login page and must come back to it. The link's token must not travel in the login address,
/// so it waits in an encrypted, HttpOnly cookie that expires in an hour and is cleared the moment the invitation is used or fails.
/// </summary>
public sealed class PendingInvitation(IDataProtectionProvider protection, IHostEnvironment environment)
{
    public const string CookieName = "invitation_pending";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("AuthService.Web.PendingInvitation").ToTimeLimitedDataProtector();

    public void Store(HttpContext context, string token) =>
        context.Response.Cookies.Append(
            CookieName,
            _protector.Protect(token, Lifetime),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = !environment.IsDevelopment(),
                MaxAge = Lifetime,
                Path = "/",
                IsEssential = true,
            });

    /// <summary>The waiting token, or null when there is none, it ran out, or the cookie was tampered with.</summary>
    public string? Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var protectedToken) || string.IsNullOrEmpty(protectedToken))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedToken);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public void Clear(HttpContext context) => context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
}
