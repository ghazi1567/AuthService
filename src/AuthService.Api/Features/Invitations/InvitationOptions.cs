namespace AuthService.Api.Features.Invitations;

/// <summary>Settings for invitations (TEN-08), bound from <c>Auth:Invitations</c>.</summary>
public sealed class InvitationOptions
{
    public const string SectionName = "Auth:Invitations";

    /// <summary>How long an invitation link works. 7 days (TEN-08).</summary>
    public int TokenDays { get; set; } = 7;
}
