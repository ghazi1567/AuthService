namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// How much of one permission a role grants (ROL-01). Ordered, so the higher value is the wider access: when a person holds
/// several roles, the effective level of a permission is the highest granted by any of them (TOK-01). There are no deny rules.
/// </summary>
public enum AccessLevel
{
    None = 0,
    ReadOnly = 1,
    CreateRead = 2,
    Full = 3,
}
