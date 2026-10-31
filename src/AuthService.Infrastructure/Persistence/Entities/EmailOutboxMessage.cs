namespace AuthService.Infrastructure.Persistence.Entities;

public enum EmailOutboxStatus
{
    /// <summary>Waiting to be sent, or waiting to be retried after a failure.</summary>
    Pending,

    Sent,

    /// <summary>Gave up after the maximum number of attempts.</summary>
    Failed,
}

/// <summary>
/// An email waiting to be sent. It is saved in the same transaction as the change that causes it (a signup, an
/// invitation), so the email exists if and only if that change committed. A background worker sends it, outside the
/// request, so a slow or failing email provider never fails the request.
/// </summary>
public sealed class EmailOutboxMessage
{
    public Guid Id { get; set; }

    public required string ToAddress { get; set; }

    public EmailTemplate Template { get; set; }

    /// <summary>
    /// The values the template needs, as JSON. It can hold a token, so it is cleared as soon as the email is sent or given
    /// up on, and tokens are short-lived anyway.
    /// </summary>
    public string? PayloadJson { get; set; }

    public EmailOutboxStatus Status { get; set; }

    public int Attempts { get; set; }

    /// <summary>The message is not sent before this time. Pushed back after each failure.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Why the last attempt failed. Never contains a token or a link.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}
