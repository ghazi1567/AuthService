namespace AuthService.Api.Features.Applications;

/// <summary>Body of <c>POST /api/auth/admin/applications</c>. Nullable so a missing field is reported as a validation error.</summary>
public sealed record RegisterApplicationRequest(string? Key, string? Name, string? JwtAudience);

/// <summary>
/// The registered product, with its client credentials. <see cref="ClientSecret"/> is in this response and nowhere else, ever:
/// only its hash is stored, so it cannot be shown again. A lost secret is replaced by rotation (APP-05), not recovered.
/// </summary>
public sealed record RegisterApplicationResponse(
    Guid Id,
    string Key,
    string Name,
    string JwtAudience,
    bool IsActive,
    string ClientId,
    string ClientSecret,
    DateTimeOffset CreatedAt);
