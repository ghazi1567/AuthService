using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.BuiltIn;

/// <summary>One permission of the built-in product's catalog.</summary>
public sealed record BuiltInPermission(string Key, string Label, string? Description = null);

/// <summary>The keys of the built-in product's permissions, so endpoints and the catalog cannot drift apart.</summary>
public static class PermissionKeys
{
    public const string SubscriptionsRead = "subscriptions.read";
    public const string MembersRead = "members.read";
    public const string MembersManage = "members.manage";
    public const string RolesRead = "roles.read";
    public const string RolesManage = "roles.manage";
    public const string RolesAssign = "roles.assign";
    public const string AccessManage = "access.manage";
}

/// <summary>
/// The Auth Service as a product of its own. A tenant admin works on their tenant (roles, members, subscriptions, invitations) with
/// a token from the ordinary token exchange (TOK-01) for this product: <c>aud</c> is <see cref="Audience"/>, <c>tid</c> the tenant,
/// and <c>permissions</c> what their roles allow here. So the Auth Service protects its own endpoints the way every other product
/// does, and the tenant always comes from the token, never from the request.
///
/// Every tenant can use it without subscribing, and without a seat: any Active member can get a token, and the Owner role holds
/// Full on the whole catalog below. Add a permission here when a new tenant-scoped endpoint needs one; it is stored at startup.
/// </summary>
public static class BuiltInApplication
{
    /// <summary>A fixed id, so clients know which <c>applicationId</c> to ask token exchange for. See the README.</summary>
    public static readonly Guid Id = new("00000000-0000-7000-8000-00000000a07a");

    public const string Key = "auth-service";

    public const string Name = "Auth Service";

    /// <summary>The <c>aud</c> of a tenant-admin token. Not the same as the audience of service tokens, which is for products.</summary>
    public const string Audience = "auth-service-tenant";

    /// <summary>Fixed and unusable: the built-in product has no client secret, so it can never get a service token.</summary>
    public const string ClientId = "cl_builtin_auth_service";

    /// <summary>The catalog. Kept in code, next to the endpoints that use it, and synchronised to the database at startup.</summary>
    public static readonly IReadOnlyList<BuiltInPermission> Permissions =
    [
        new(PermissionKeys.SubscriptionsRead, "View subscriptions", "See which products the tenant is subscribed to, and how many seats are used."),
        new(PermissionKeys.MembersRead, "View members", "See who belongs to the tenant, their roles and the products they use."),
        new(PermissionKeys.MembersManage, "Manage members", "Enable and disable the memberships of the tenant's people."),
        new(PermissionKeys.RolesRead, "View roles", "See the tenant's roles and what each one grants."),
        new(PermissionKeys.RolesManage, "Manage roles", "Create, change and delete the tenant's roles and decide what each role grants."),
        new(PermissionKeys.RolesAssign, "Assign roles", "Decide which roles each member of the tenant holds."),
        new(PermissionKeys.AccessManage, "Manage product access", "Give members access to the products the tenant subscribes to, which uses a seat, and take it away."),
    ];
}
