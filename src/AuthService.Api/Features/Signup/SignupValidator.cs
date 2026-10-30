using System.Net.Mail;

namespace AuthService.Api.Features.Signup;

/// <summary>A signup request that passed validation, with the email already normalised.</summary>
public sealed record ValidSignup(string TenantName, string Email, string Password, string? DisplayName);

public static class SignupValidator
{
    public const int MaxTenantNameLength = 200;
    public const int MaxDisplayNameLength = 200;
    public const int MaxEmailLength = 254;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    /// <summary>Emails are stored trimmed and lowercased; this is the only place that rule lives.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static bool TryValidate(
        SignupRequest request,
        out ValidSignup? valid,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = null;

        var tenantName = request.TenantName?.Trim();
        if (string.IsNullOrEmpty(tenantName))
        {
            errors[nameof(SignupRequest.TenantName)] = ["Tenant name is required."];
        }
        else if (tenantName.Length > MaxTenantNameLength)
        {
            errors[nameof(SignupRequest.TenantName)] = [$"Tenant name must be at most {MaxTenantNameLength} characters."];
        }

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : NormalizeEmail(request.Email);
        if (email is null)
        {
            errors[nameof(SignupRequest.Email)] = ["Email is required."];
        }
        else if (!IsPlausibleEmail(email))
        {
            errors[nameof(SignupRequest.Email)] = ["Email is not a valid email address."];
        }

        // No composition rules (NIST SP 800-63B): length is what matters. Not trimmed; spaces are valid password characters.
        var password = request.Password;
        if (string.IsNullOrEmpty(password))
        {
            errors[nameof(SignupRequest.Password)] = ["Password is required."];
        }
        else if (password.Length is < MinPasswordLength or > MaxPasswordLength)
        {
            errors[nameof(SignupRequest.Password)] =
                [$"Password must be between {MinPasswordLength} and {MaxPasswordLength} characters."];
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        if (displayName is { Length: > MaxDisplayNameLength })
        {
            errors[nameof(SignupRequest.DisplayName)] = [$"Display name must be at most {MaxDisplayNameLength} characters."];
        }

        if (errors.Count > 0)
        {
            return false;
        }

        valid = new ValidSignup(tenantName!, email!, password!, displayName);
        return true;
    }

    private static bool IsPlausibleEmail(string email) =>
        email.Length <= MaxEmailLength
        && MailAddress.TryCreate(email, out var parsed)
        && parsed.Address == email
        && parsed.Host.Contains('.');
}
