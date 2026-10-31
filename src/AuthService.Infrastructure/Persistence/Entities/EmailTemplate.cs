namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>The emails the service sends. Each has one fixed template.</summary>
public enum EmailTemplate
{
    Verification,
    PasswordReset,
    Invitation,
}
