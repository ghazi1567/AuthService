namespace AuthService.Api.Email;

/// <summary>A fully rendered email, ready for a provider to send.</summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>The one interface every provider implements. Everything that sends email goes through it.</summary>
public interface IEmailSender
{
    /// <summary>Sends the message, or throws if the provider did not accept it.</summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// A provider refused or failed to send. The message is safe to store and log: it never includes the email's content,
/// a token or a link.
/// </summary>
public sealed class EmailSendException(string message) : Exception(message);
