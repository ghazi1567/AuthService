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

/// <summary>A role of the organisation, as the role list shows it.</summary>
public sealed record RoleListItem(Guid Id, string Name, bool IsBuiltIn, int MemberCount);

/// <summary>A role as it is shown on a member or an invitation.</summary>
public sealed record AssignedRole(Guid Id, string Name, bool IsBuiltIn);

/// <summary>An invitation nobody has accepted yet. The link itself is only in the email.</summary>
public sealed record PendingInvitationItem(
    Guid MembershipId,
    string Email,
    IReadOnlyList<AssignedRole> Roles,
    IReadOnlyList<Guid> ApplicationIds,
    string InvitedBy,
    DateTimeOffset InvitedAt,
    DateTimeOffset LastSentAt,
    DateTimeOffset ExpiresAt,
    bool IsExpired);

/// <summary>One of the organisation's product subscriptions, with how many seats are used.</summary>
public sealed record SubscriptionItem(
    Guid ApplicationId,
    string ApplicationKey,
    string ApplicationName,
    string Status,
    int? MaxSeats,
    int SeatsUsed)
{
    public bool IsActive => Status == "Active";

    public bool HasFreeSeat => MaxSeats is null || SeatsUsed < MaxSeats;
}

/// <summary>Body of <c>POST /api/auth/tenants/me/invitations</c>: one person, one to ten roles, optional products.</summary>
public sealed record InviteRequest(string Email, IReadOnlyList<Guid> RoleIds, IReadOnlyList<Guid> ApplicationIds);

