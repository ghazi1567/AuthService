namespace AuthService.Api.Features.Logout;

/// <summary>Body of <c>POST /api/auth/logout</c>. Nullable so a missing token is reported as a validation error.</summary>
public sealed record LogoutRequest(string? RefreshToken);
