using System.Text;

namespace AuthService.Web.Api;

/// <summary>
/// Body of the API's <c>POST /api/auth/invitations/accept</c>. A password is sent only by a person who is new to the platform; someone
/// with an account accepts while signed in and sends none. It carries a link token and maybe a password, so it prints nothing.
/// </summary>
public sealed record AcceptInvitationRequest(string Token, string? Password)
{
    private bool PrintMembers(StringBuilder builder) => false;
}
