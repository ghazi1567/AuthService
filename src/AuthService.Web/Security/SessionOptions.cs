using System.ComponentModel.DataAnnotations;

namespace AuthService.Web.Security;

/// <summary>How long a signed-in session lives and when its access token is renewed. Bound from <c>AuthService:Session</c>.</summary>
public sealed class SessionOptions
{
    public const string SectionName = "AuthService:Session";

    /// <summary>The cookie that carries the session id (never a token).</summary>
    [Required]
    public string CookieName { get; set; } = "auth_session";

    /// <summary>How long a session lasts when the person chose "Keep me signed in". The API's refresh token lasts 30 days.</summary>
    [Range(1, 30)]
    public int RememberedDays { get; set; } = 30;

    /// <summary>How long a session lasts otherwise. The cookie also ends when the browser closes.</summary>
    [Range(1, 24 * 30)]
    public int DefaultHours { get; set; } = 12;

    /// <summary>The access token is renewed when it has this many seconds or fewer left.</summary>
    [Range(0, 600)]
    public int RefreshBeforeSeconds { get; set; } = 60;
}
