namespace AuthService.Web.Api;

/// <summary>Body of the API's <c>POST /api/auth/login</c>.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>
/// What the API sends back for a successful sign-in. Only the fields the web app reads are listed; the memberships the API also sends
/// arrive with the organisation picker.
/// </summary>
public sealed record LoginResponse(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken);
