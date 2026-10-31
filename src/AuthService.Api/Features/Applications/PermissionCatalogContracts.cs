namespace AuthService.Api.Features.Applications;

/// <summary>One controllable action in a request. Nullable so a missing field is reported as a validation error.</summary>
public sealed record PermissionInput(string? Key, string? Label, string? Description);

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/permissions</c>.</summary>
public sealed record RegisterPermissionsRequest(IReadOnlyList<PermissionInput>? Permissions);

public sealed record PermissionDto(Guid Id, string Key, string Label, string? Description);

/// <summary>
/// What a registration did, and the product's whole catalog afterwards. Registering never removes a permission: a role may
/// grant it, so a key a product stops using stays in the catalog.
/// </summary>
public sealed record RegisterPermissionsResponse(
    Guid ApplicationId,
    int Created,
    int Updated,
    int Unchanged,
    IReadOnlyList<PermissionDto> Permissions);

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/active</c>.</summary>
public sealed record SetApplicationActiveRequest(bool? IsActive);

public sealed record ApplicationStatusResponse(Guid Id, string Key, bool IsActive);
