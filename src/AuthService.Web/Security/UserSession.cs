using System.Text;

namespace AuthService.Web.Security;

/// <summary>
/// One person's signed-in session, kept on the server. The browser only ever holds the <see cref="Id"/>. Printing it (a log line, an
/// exception) shows the id and nothing else: the tokens must never reach a log.
/// </summary>
public sealed record UserSession(
    string Id,
    Guid UserId,
    string Email,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset ExpiresAt)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id);
        return true;
    }
}
