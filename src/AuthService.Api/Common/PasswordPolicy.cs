namespace AuthService.Api.Common;

/// <summary>
/// The one rule for what a password may be, used wherever a person chooses one (signup, password reset). No composition
/// rules (NIST SP 800-63B): length is what matters. A password is never trimmed, since spaces are valid characters.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    /// <summary>The reason a password is not acceptable, or null if it is.</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "Password is required.";
        }

        return password.Length is < MinLength or > MaxLength
            ? $"Password must be between {MinLength} and {MaxLength} characters."
            : null;
    }
}
