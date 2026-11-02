using System.Text;

namespace AuthService.Web.Api;

// These records carry passwords and tokens. Printing one (a log line, an exception message) must never show them, so each prints only
// what is safe to read.

/// <summary>Body of the API's <c>POST /api/auth/login</c>.</summary>
public sealed record LoginRequest(string Email, string Password)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Email = ").Append(Email);
        return true;
    }
}

/// <summary>
/// What the API sends back for a successful sign-in. Only the fields the web app reads are listed; the memberships the API also sends
/// arrive with the organisation picker.
/// </summary>
public sealed record LoginResponse(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ExpiresIn = ").Append(ExpiresIn);
        return true;
    }
}

/// <summary>Body of <c>POST /api/auth/refresh</c>. The refresh token works once.</summary>
public sealed record RefreshRequest(string RefreshToken)
{
    private bool PrintMembers(StringBuilder builder) => false;
}

/// <summary>A new access token and the next refresh token.</summary>
public sealed record RefreshResponse(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ExpiresIn = ").Append(ExpiresIn);
        return true;
    }
}

/// <summary>Body of <c>POST /api/auth/logout</c>.</summary>
public sealed record LogoutRequest(string RefreshToken)
{
    private bool PrintMembers(StringBuilder builder) => false;
}
