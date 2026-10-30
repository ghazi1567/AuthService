using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Login;

/// <summary>Body of <c>POST /api/auth/login</c>. Both fields are nullable so a missing one is reported as a validation error.</summary>
public sealed record LoginRequest(string? Email, string? Password);

/// <summary>One tenant the person belongs to, so the client can choose which to work in (token exchange, TOK-01).</summary>
public sealed record MembershipSummary(Guid TenantId, string TenantName, MembershipStatus Status);

/// <summary>
/// Returned on a successful login. The access token is an identity token: it names the user only, with no tenant,
/// permissions or audience.
/// </summary>
public sealed record LoginResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken,
    IReadOnlyList<MembershipSummary> Memberships);
