namespace AuthService.Web.Api;

/// <summary>Body of <c>POST /api/auth/token/exchange</c>: trade the person's sign-in for a token for one product in one organisation.</summary>
public sealed record TokenExchangeRequest(Guid TenantId, Guid ApplicationId);

/// <summary>A product token. It is only held on the server, in the console token cache.</summary>
public sealed record TokenExchangeResponse(string AccessToken, string TokenType, int ExpiresIn)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("ExpiresIn = ").Append(ExpiresIn);
        return true;
    }
}

public sealed record MemberRole(Guid Id, string Name);

public sealed record MemberApplication(Guid ApplicationId, string ApplicationKey);

/// <summary>A person's membership in the organisation, with their roles and the products they have a seat in.</summary>
public sealed record MemberItem(
    Guid MembershipId,
    string Email,
    string? DisplayName,
    string Status,
    IReadOnlyList<MemberRole> Roles,
    IReadOnlyList<MemberApplication> Applications,
    DateTimeOffset CreatedAt);

/// <summary>Body of <c>PATCH /api/auth/tenants/me/members/{id}</c>: "Active" or "Disabled".</summary>
public sealed record SetMemberStatusRequest(string Status);
