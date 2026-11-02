namespace AuthService.Web.Api;

/// <summary>Body of the API's <c>POST /api/auth/email/verify</c>. The token comes from an email link, so it is never printed.</summary>
public sealed record VerifyEmailRequest(string Token)
{
    private bool PrintMembers(System.Text.StringBuilder builder) => false;
}
