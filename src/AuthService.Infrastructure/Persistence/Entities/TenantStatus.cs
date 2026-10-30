namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>Platform-level status of a tenant, independent of any single product's subscription status.</summary>
public enum TenantStatus
{
    Trial,
    Active,
    Suspended,
}
