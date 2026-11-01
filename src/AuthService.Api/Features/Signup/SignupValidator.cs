using System.Net.Mail;
using AuthService.Api.Common;

namespace AuthService.Api.Features.Signup;

/// <summary>A signup request that passed validation, with the email already normalised.</summary>
public sealed record ValidSignup(string TenantName, string Email, string Password, string? DisplayName);

public static class SignupValidator
{
    public const int MaxTenantNameLength = 200;
    public const int MaxDisplayNameLength = 200;
    public const int MaxEmailLength = 254;

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

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : EmailNormalizer.Normalize(request.Email);
        if (email is null)
        {
            errors[nameof(SignupRequest.Email)] = ["Email is required."];
        }
        else if (!IsPlausibleEmail(email))
        {
            errors[nameof(SignupRequest.Email)] = ["Email is not a valid email address."];
        }

        var password = request.Password;
        if (PasswordPolicy.Validate(password) is { } passwordError)
        {
            errors[nameof(SignupRequest.Password)] = [passwordError];
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

    public static bool IsPlausibleEmail(string email) =>
        email.Length <= MaxEmailLength
        && MailAddress.TryCreate(email, out var parsed)
        && parsed.Address == email
        && parsed.Host.Contains('.');
}
