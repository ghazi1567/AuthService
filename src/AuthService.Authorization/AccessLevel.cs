namespace AuthService.Authorization;

/// <summary>
/// How much of one permission a token grants. Ordered, so the higher value is the wider access. These are the four levels the Auth
/// Service issues in the <c>permissions</c> claim (<c>key:Level</c>); a name that is not one of them grants nothing.
/// </summary>
public enum AccessLevel
{
    /// <summary>No access. Never present in a token, but a valid level to ask for.</summary>
    None = 0,

    /// <summary>May read.</summary>
    ReadOnly = 1,

    /// <summary>May create and read.</summary>
    CreateRead = 2,

    /// <summary>May do everything the permission covers.</summary>
    Full = 3,
}
