namespace AuthService.Api.Features.Invitations;

/// <summary>Settings for invitations (TEN-08), bound from <c>Auth:Invitations</c>.</summary>
public sealed class InvitationOptions
{
    public const string SectionName = "Auth:Invitations";

    /// <summary>How long an invitation link works. 7 days (TEN-08).</summary>
    public int TokenDays { get; set; } = 7;

    /// <summary>How long after a link is emailed it cannot be emailed again, so resending cannot be used to flood an inbox. 60 seconds.</summary>
    public int ResendCooldownSeconds { get; set; } = 60;
}
