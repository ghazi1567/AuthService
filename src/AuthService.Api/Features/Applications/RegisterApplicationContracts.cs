namespace AuthService.Api.Features.Applications;

/// <summary>
/// Body of <c>POST /api/auth/admin/applications</c>. Nullable so a missing field is reported as a validation error.
/// <see cref="LaunchUrl"/>, <see cref="Permissions"/> and <see cref="Activate"/> are optional (ONB-01): sent together they register a
/// product, its catalogue and launch address, and make it available in one call, saved all or nothing. A caller that sends only the
/// first three gets what it always got.
/// </summary>
public sealed record RegisterApplicationRequest(
    string? Key,
    string? Name,
    string? JwtAudience,
    string? LaunchUrl = null,
    IReadOnlyList<PermissionInput>? Permissions = null,
    bool? Activate = null);

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
    DateTimeOffset CreatedAt,
    string? LaunchUrl = null,
    int PermissionCount = 0,
    string Credential = "");

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/name</c>. Nullable so a missing name is a validation error.</summary>
public sealed record RenameApplicationRequest(string? Name);

/// <summary>The product's name after a rename. Its key and audience never change.</summary>
public sealed record RenameApplicationResponse(Guid Id, string Key, string Name);
