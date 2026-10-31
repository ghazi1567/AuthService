using System.Text;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Email.Providers;

/// <summary>
/// Development provider: writes each email to a file instead of sending it, so a developer can open the verification
/// link. Writing to a file, not the log, keeps tokens and links out of logs.
/// </summary>
public sealed class FileEmailSender(IOptions<EmailOptions> options, IHostEnvironment environment) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var directory = options.Value.FileDirectory
            ?? Path.Combine(environment.ContentRootPath, "artifacts", "emails");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.txt");
        var content = new StringBuilder()
            .Append("To: ").AppendLine(message.To)
            .Append("Subject: ").AppendLine(message.Subject)
            .AppendLine()
            .AppendLine(message.TextBody)
            .ToString();

        await File.WriteAllTextAsync(path, content, cancellationToken);
    }
}
