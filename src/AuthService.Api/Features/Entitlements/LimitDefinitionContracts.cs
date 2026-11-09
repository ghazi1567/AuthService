using System.Text.Json;

namespace AuthService.Api.Features.Entitlements;

/// <summary>
/// One limit in a registration request. Everything is nullable so a missing field is reported as a validation error.
/// <paramref name="DefaultValue"/> is kept as raw JSON so that leaving it out (an error) can be told apart from <c>null</c> (unlimited).
/// </summary>
public sealed record LimitInput(string? Key, string? Label, string? Description, string? Unit, JsonElement DefaultValue);

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/limits</c>.</summary>
public sealed record RegisterLimitsRequest(IReadOnlyList<LimitInput>? Limits);

/// <summary>A limit definition as the platform admin and the product read it. <c>DefaultValue</c> null means unlimited.</summary>
public sealed record LimitDefinitionDto(
    string Key,
    string Label,
    string? Description,
    string? Unit,
    long? DefaultValue,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// What a registration did, and the product's whole set of limits afterwards. Registering never removes a limit: plans and overrides
/// refer to it by key, so a key a product stops using stays.
/// </summary>
public sealed record RegisterLimitsResponse(
    Guid ApplicationId,
    int Created,
    int Updated,
    int Unchanged,
    IReadOnlyList<LimitDefinitionDto> Limits);
