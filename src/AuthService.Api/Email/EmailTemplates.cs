using System.Net;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Email;

/// <summary>
/// Turns a template name and its values into the email a person reads. Links carry a token and point at
/// <see cref="EmailOptions.PublicBaseUrl"/>; the page there sends the token back to the API.
/// </summary>
public static class EmailTemplates
{
    public static EmailMessage Render(
        EmailTemplate template,
        string to,
        IReadOnlyDictionary<string, string> data,
        EmailOptions options)
    {
        return template switch
        {
            EmailTemplate.Verification => WithLink(
                to,
                "Verify your email address",
                "Welcome! Please confirm your email address to finish setting up your account.",
                "Verify email address",
                Link(options, "verify-email", data),
                $"This link expires in {ValidFor(data, "24 hours")}. If you did not sign up, you can ignore this email."),

            EmailTemplate.PasswordReset => WithLink(
                to,
                "Reset your password",
                "We received a request to reset your password.",
                "Reset password",
                Link(options, "reset-password", data),
                $"This link expires in {ValidFor(data, "1 hour")} and can be used once. If you did not ask for this, you can ignore this email."),

            EmailTemplate.Invitation => WithLink(
                to,
                $"You have been invited to {Required(data, "tenantName")}",
                $"You have been invited to join {Required(data, "tenantName")}.",
                "Accept invitation",
                Link(options, "accept-invitation", data),
                $"This invitation expires in {ValidFor(data, "7 days")}. If you were not expecting it, you can ignore this email."),

            _ => throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown email template."),
        };
    }

    private static string Link(EmailOptions options, string path, IReadOnlyDictionary<string, string> data) =>
        $"{options.PublicBaseUrl.TrimEnd('/')}/{path}?token={Uri.EscapeDataString(Required(data, "token"))}";

    /// <summary>
    /// The lifetime the link was made with, as queued by the feature that made it. An email queued before that was recorded falls back
    /// to the lifetime the setting had by default.
    /// </summary>
    private static string ValidFor(IReadOnlyDictionary<string, string> data, string fallback) =>
        data.TryGetValue(EmailLifetime.DataKey, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static string Required(IReadOnlyDictionary<string, string> data, string key) =>
        data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"The email template needs a '{key}' value.");

    private static EmailMessage WithLink(string to, string subject, string intro, string action, string link, string footer)
    {
        var html =
            $"<p>{WebUtility.HtmlEncode(intro)}</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(action)}</a></p>" +
            $"<p>{WebUtility.HtmlEncode(footer)}</p>";

        var text = $"{intro}{Environment.NewLine}{Environment.NewLine}{action}: {link}{Environment.NewLine}{Environment.NewLine}{footer}";

        return new EmailMessage(to, subject, html, text);
    }
}
