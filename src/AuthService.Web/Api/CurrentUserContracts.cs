using System.Text.Json.Serialization;

namespace AuthService.Web.Api;

/// <summary>Whether a person can enter an organisation they belong to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MembershipStatus
{
    /// <summary>Invited but has not accepted yet, so cannot enter.</summary>
    Invited,

    Active,

    /// <summary>An admin turned this person's access off.</summary>
    Disabled,
}

/// <summary>One organisation a person belongs to. The API sends no role here, only the status.</summary>
public sealed record MembershipSummary(Guid TenantId, string TenantName, MembershipStatus Status);

/// <summary>The API's <c>GET /api/auth/users/me</c>: who is signed in and which organisations they belong to.</summary>
public sealed record CurrentUser(Guid Id, string Email, string? DisplayName, bool EmailVerified, IReadOnlyList<MembershipSummary> Memberships);

/// <summary>Body of <c>POST /api/auth/tenants</c>: another organisation, owned by the signed-in person.</summary>
public sealed record CreateTenantRequest(string TenantName);

public sealed record CreateTenantResponse(Guid TenantId, string TenantName, Guid MembershipId);
