namespace AuthService.Api.Features.Tenants;

/// <summary>Body of <c>POST /api/auth/tenants</c>. Nullable so a missing name is reported as a validation error.</summary>
public sealed record CreateTenantRequest(string? TenantName);

public sealed record CreateTenantResponse(Guid TenantId, string TenantName, Guid MembershipId);
