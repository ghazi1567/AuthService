using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Email.Providers;

/// <summary>Sends mail through Brevo's transactional email API (free tier first, per NTF-01).</summary>
public sealed class BrevoEmailSender(HttpClient httpClient, IOptions<EmailOptions> options) : IEmailSender
{
    public const string HttpClientName = "brevo";

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/smtp/email")
        {
            Content = JsonContent.Create(new
            {
                sender = new { name = settings.FromName, email = settings.FromAddress },
                to = new[] { new { email = message.To } },
                subject = message.Subject,
                htmlContent = message.HtmlBody,
                textContent = message.TextBody,
            }),
        };
        request.Headers.Add("api-key", settings.BrevoApiKey);
        request.Headers.Add("accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new EmailSendException("Brevo could not be reached.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmailSendException("Brevo did not answer in time.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Only the status code: Brevo's response body is not needed and is never stored or logged.
                throw new EmailSendException($"Brevo returned {(int)response.StatusCode}.");
            }
        }
    }
}
