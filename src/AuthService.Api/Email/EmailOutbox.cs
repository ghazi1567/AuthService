using System.Text.Json;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Email;

/// <summary>
/// Queues an email for sending. It only adds the message to the database context: the caller's own
/// <c>SaveChangesAsync</c> commits it together with the change that caused it (a signup, an invitation), so the
/// email exists if and only if that change committed. Sending happens later, outside the request.
/// </summary>
public sealed class EmailOutbox(AuthDbContext db, IOptions<EmailOptions> options, TimeProvider timeProvider)
{
    public void Enqueue(EmailTemplate template, string toAddress, IReadOnlyDictionary<string, string> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toAddress);

        // Fail now, in the request that has the mistake, rather than after the email has been retried for hours.
        EmailTemplates.Render(template, toAddress, data, options.Value);

        var now = timeProvider.GetUtcNow();
        db.EmailOutbox.Add(new EmailOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            ToAddress = toAddress,
            Template = template,
            PayloadJson = JsonSerializer.Serialize(data),
            Status = EmailOutboxStatus.Pending,
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now,
        });
    }
}
