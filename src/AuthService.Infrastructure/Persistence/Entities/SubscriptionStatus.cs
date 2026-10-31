namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>Whether a tenant may use a product. Only an Active subscription lets the tenant's people get tokens for it (TOK-01).</summary>
public enum SubscriptionStatus
{
    Active,
    Suspended,
    Cancelled,
}
