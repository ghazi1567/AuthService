using System.Collections.Concurrent;
using AuthService.Api.Email;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Ntf01;

/// <summary>An email provider that refuses the first few sends, then accepts. Use a huge count to make it always fail.</summary>
public sealed class FlakyEmailSender(int failFirst) : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _attempts) <= failFirst)
        {
            throw new EmailSendException("Brevo returned 503.");
        }

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

internal static class EmailTestSupport
{
    /// <summary>Queues an email the way a feature would: add it, then save.</summary>
    public static async Task EnqueueAsync(
        AuthApiHarness harness,
        EmailTemplate template,
        string to,
        Dictionary<string, string> data,
        CancellationToken ct)
    {
        await using var scope = harness.CreateScope();
        scope.ServiceProvider.GetRequiredService<EmailOutbox>().Enqueue(template, to, data);
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().SaveChangesAsync(ct);
    }

    public static async Task<List<EmailOutboxMessage>> LoadOutboxAsync(AuthApiHarness harness, CancellationToken ct)
    {
        await using var scope = harness.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>()
            .EmailOutbox.AsNoTracking().OrderBy(message => message.CreatedAt).ToListAsync(ct);
    }

    public static Task WaitUntilStatusAsync(AuthApiHarness harness, EmailOutboxStatus status, int count, CancellationToken ct) =>
        Eventually.UntilAsync(
            async () => (await LoadOutboxAsync(harness, ct)).Count(message => message.Status == status) == count,
            $"{count} email(s) are {status}");
}
