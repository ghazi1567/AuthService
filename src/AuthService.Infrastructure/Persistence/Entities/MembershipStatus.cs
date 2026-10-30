namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// Status of a person's membership in one tenant. Invited and Active memberships with product access
/// count as seats; Disabled ones do not.
/// </summary>
public enum MembershipStatus
{
    Invited,
    Active,
    Disabled,
}
