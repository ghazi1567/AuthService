namespace AuthService.Api.Features.ApiKeys;

/// <summary>Settings for API keys (KEY-02), bound from <c>Auth:ApiKeys</c>.</summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "Auth:ApiKeys";

    /// <summary>How long a key keeps working after it is rotated out, unless the rotation asks for another time. 24 hours.</summary>
    public int RotationGraceHours { get; set; } = 24;
}

/// <summary>One permission a new key should have. A missing level means Full.</summary>
public sealed record ApiKeyPermissionInput(string? Permission, string? Level);

/// <summary>Body of <c>POST /api/auth/apikeys</c>.</summary>
public sealed record CreateApiKeyRequest(string? Name, Guid? ApplicationId, IReadOnlyList<ApiKeyPermissionInput>? Permissions);

/// <summary>Body of <c>POST /api/auth/apikeys/{id}/rotate</c>. The whole body is optional.</summary>
public sealed record RotateApiKeyRequest(int? GraceHours);

public sealed record ApiKeyScopeDto(string Permission, string Level);

/// <summary>A key as it is listed: masked, never the raw value. <see cref="Status"/> is Active, Expiring (being rotated out), Expired or Revoked.</summary>
public sealed record ApiKeySummary(
    Guid Id,
    string Name,
    string MaskedKey,
    Guid? ApplicationId,
    string? ApplicationKey,
    IReadOnlyList<ApiKeyScopeDto> Permissions,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

/// <summary><see cref="RawKey"/> is in this response and nowhere else, ever: only a salted hash is stored, so it cannot be shown again.</summary>
public sealed record CreatedApiKeyResponse(ApiKeySummary Key, string RawKey);

/// <summary>What a product learns about a valid key: whose it is and what it may do.</summary>
public sealed record ValidatedApiKey(Guid TenantId, Guid KeyId, string Name, Guid? ApplicationId, IReadOnlyList<ApiKeyScopeDto> Permissions);

/// <summary>Body of <c>POST /api/auth/apikeys/validate</c>. <c>permission</c> and <c>level</c> ask whether the key may do one thing.</summary>
public sealed record ValidateApiKeyRequest(string? Key, string? Permission, string? Level);
