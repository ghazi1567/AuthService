namespace AuthService.Api.Features.Refresh;

/// <summary>Body of <c>POST /api/auth/refresh</c>. Nullable so a missing token is reported as a validation error.</summary>
public sealed record RefreshRequest(string? RefreshToken);

/// <summary>
/// A new identity access token and the next refresh token. The refresh token in the request is now spent and can
/// never be used again.
/// </summary>
public sealed record RefreshResponse(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken);
