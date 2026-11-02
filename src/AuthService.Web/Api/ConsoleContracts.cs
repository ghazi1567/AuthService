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
public sealed record RoleListItem(
    Guid Id,
    string Name,
    bool IsBuiltIn,
    int MemberCount,
    IReadOnlyList<RoleGrant>? Permissions = null,
    DateTimeOffset? CreatedAt = null)
{
    /// <summary>How many permissions the role grants (a level of None grants nothing).</summary>
    public int PermissionCount => Permissions?.Count(grant => grant.Level != "None") ?? 0;

    public int ProductCount => Permissions?.Where(grant => grant.Level != "None").Select(grant => grant.ApplicationId).Distinct().Count() ?? 0;
}

/// <summary>One permission a role grants in one product, at a level (None, ReadOnly, CreateRead or Full).</summary>
public sealed record RoleGrant(Guid ApplicationId, string ApplicationKey, string Permission, string Level);

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


/// <summary>One permission of a product that a role can grant.</summary>
public sealed record CatalogPermission(string Key, string Label, string? Description);

/// <summary>A product whose permissions the organisation's roles can grant (the Auth Service itself, and every product it uses).</summary>
public sealed record CatalogApplication(Guid ApplicationId, string Key, string Name, bool IsBuiltIn, IReadOnlyList<CatalogPermission> Permissions);

/// <summary>What a role can grant: the levels, and the permissions of each product in use.</summary>
public sealed record PermissionCatalog(IReadOnlyList<string> Levels, IReadOnlyList<CatalogApplication> Applications);

/// <summary>One permission a role grants, as sent to the API.</summary>
public sealed record RoleGrantInput(Guid ApplicationId, string Permission, string Level);

/// <summary>Body of <c>POST /api/auth/roles</c> and <c>PATCH /api/auth/roles/{id}</c>. The permissions replace the whole set.</summary>
public sealed record SaveRoleRequest(string Name, IReadOnlyList<RoleGrantInput> Permissions);
