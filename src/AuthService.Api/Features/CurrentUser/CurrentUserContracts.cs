using AuthService.Api.Features.Accounts;

namespace AuthService.Api.Features.CurrentUser;

/// <summary>
/// The caller's own profile and tenant memberships. It has no field for credentials, so nothing secret can leak into it.
/// </summary>
public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string? DisplayName,
    bool EmailVerified,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MembershipSummary> Memberships);
