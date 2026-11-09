using AuthService.Api.Email.Providers;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Email;

/// <summary>Picks the email provider from configuration, so switching provider never needs a code change.</summary>
public static class EmailSenderFactory
{
    public static IEmailSender Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<EmailOptions>>();
        var environment = services.GetRequiredService<IHostEnvironment>();
        var settings = options.Value;

        var provider = settings.Provider;
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = environment.IsDevelopment() ? "File"
                : environment.IsEnvironment("Testing") ? "InMemory"
                : throw new InvalidOperationException($"{EmailOptions.SectionName}:{nameof(EmailOptions.Provider)} is not configured.");
        }

        switch (provider.Trim().ToLowerInvariant())
        {
            case "brevo":
                if (string.IsNullOrWhiteSpace(settings.BrevoApiKey))
                {
                    throw new InvalidOperationException($"{EmailOptions.SectionName}:{nameof(EmailOptions.BrevoApiKey)} is not configured.");
                }

                if (string.IsNullOrWhiteSpace(settings.FromAddress) || settings.FromAddress == EmailOptions.DefaultFromAddress)
                {
                    throw new InvalidOperationException($"{EmailOptions.SectionName}:{nameof(EmailOptions.FromAddress)} must be set to a verified sender for Brevo.");
                }

                // Links in real emails go to real people: the default points at localhost, and a plain-http link would carry the token in the clear.
                if (!Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidOperationException(
                        $"{EmailOptions.SectionName}:{nameof(EmailOptions.PublicBaseUrl)} must be an absolute https address (the web app that handles the links) when {EmailOptions.SectionName}:{nameof(EmailOptions.Provider)} is Brevo.");
                }

                return new BrevoEmailSender(
                    services.GetRequiredService<IHttpClientFactory>().CreateClient(BrevoEmailSender.HttpClientName),
                    options);

            case "file":
                return new FileEmailSender(options, environment);

            case "inmemory":
                return services.GetRequiredService<InMemoryEmailSender>();

            default:
                throw new InvalidOperationException($"{EmailOptions.SectionName}:{nameof(EmailOptions.Provider)} '{provider}' is not supported (use Brevo, File or InMemory).");
        }
    }
}
