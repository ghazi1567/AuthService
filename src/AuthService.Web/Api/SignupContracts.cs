using System.Text;

namespace AuthService.Web.Api;

/// <summary>What the web app knows about the API's password rule, so the page can say it before the person submits.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;
}

/// <summary>Body of the API's <c>POST /api/auth/signup</c>. It carries a password, so it prints only safe fields.</summary>
public sealed record SignupRequest(string TenantName, string Email, string Password, string? DisplayName)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Email = ").Append(Email);
        return true;
    }
}

/// <summary>Body of <c>POST /api/auth/email/verify/resend</c>.</summary>
public sealed record ResendVerificationRequest(string Email);
