using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Accounts;

public static class AccountAccess
{
    /// <summary>
    /// "Disabled" is a property of memberships (TEN-07), not of the person. A person who belongs to at least one tenant
    /// but has no usable membership left is treated as disabled: they cannot log in or refresh a session.
    /// </summary>
    public static bool IsDisabled(IReadOnlyCollection<MembershipStatus> membershipStatuses) =>
        membershipStatuses.Count > 0 && membershipStatuses.All(status => status == MembershipStatus.Disabled);
}
