using System.Text;

namespace AuthService.Web.Api;

/// <summary>Body of the API's <c>POST /api/auth/password/forgot</c>.</summary>
public sealed record ForgotPasswordRequest(string Email);

/// <summary>Body of <c>POST /api/auth/password/reset</c>. It carries a link token and a password, so it prints nothing.</summary>
public sealed record ResetPasswordRequest(string Token, string NewPassword)
{
    private bool PrintMembers(StringBuilder builder) => false;
}
