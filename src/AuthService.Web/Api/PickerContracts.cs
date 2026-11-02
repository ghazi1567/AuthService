namespace AuthService.Web.Api;

/// <summary>Where a product stands for the signed-in person in one organisation (<c>GET /api/auth/users/me/applications</c>).</summary>
public static class ProductState
{
    public const string Available = "Available";
    public const string NoAccess = "NoAccess";
    public const string NotSubscribed = "NotSubscribed";
    public const string SubscriptionSuspended = "SubscriptionSuspended";
    public const string SubscriptionCancelled = "SubscriptionCancelled";
}

/// <summary>One product as the picker shows it. The launch address comes only for a product the person can open.</summary>
public sealed record MyProductItem(Guid ApplicationId, string Key, string Name, string State, string? LaunchUrl);
