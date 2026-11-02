using System.Text.Json;
using AuthService.Web.Api;

namespace AuthService.Web.Security;

public enum PlatformOutcome
{
    /// <summary>The API accepted this person as a platform admin.</summary>
    Allowed,

    /// <summary>The API refused: this person is not a platform admin.</summary>
    NotAllowed,

    /// <summary>The sign-in ended or the API no longer accepts it: sign in again.</summary>
    SessionEnded,

    /// <summary>The API could not be reached or had a problem.</summary>
    Unavailable,
}

public sealed record PlatformResult(PlatformOutcome Outcome, string? Token = null)
{
    public override string ToString() => $"Platform {Outcome}";
}

/// <summary>
/// Opens the platform console for the signed-in person. Whether someone is a platform admin is decided by the API alone: the web app
/// asks it with a cheap read that only platform admins may make, and never trusts a claim, cookie or address from the browser.
/// One answer per request, so the layout and the page share it. The token is the person's own sign-in token and is never printed.
/// </summary>
public sealed class PlatformSession(AuthApiClient api, SessionService sessions)
{
    /// <summary>The cheapest read behind the platform-admin policy that is not itself recorded in the audit log.</summary>
    private const string ProbePath = "/api/auth/admin/webhooks?limit=1";

    private Task<PlatformResult>? _loaded;

    public Task<PlatformResult> LoadAsync(HttpContext httpContext) => _loaded ??= OpenAsync(httpContext);

    private async Task<PlatformResult> OpenAsync(HttpContext httpContext)
    {
        if (httpContext.User.FindFirst(SessionAuthentication.SessionIdClaim) is not { } sid)
        {
            return new PlatformResult(PlatformOutcome.SessionEnded);
        }

        var token = await sessions.GetAccessTokenAsync(sid.Value, httpContext.RequestAborted);
        if (token is null)
        {
            return new PlatformResult(PlatformOutcome.SessionEnded);
        }

        var probe = await api.GetAsync<JsonElement>(ProbePath, token, httpContext.RequestAborted);
        if (probe.IsSuccess)
        {
            return new PlatformResult(PlatformOutcome.Allowed, token);
        }

        return new PlatformResult(probe.Problem!.Status switch
        {
            StatusCodes.Status401Unauthorized => PlatformOutcome.SessionEnded,
            StatusCodes.Status403Forbidden => PlatformOutcome.NotAllowed,
            _ => PlatformOutcome.Unavailable,
        });
    }
}
