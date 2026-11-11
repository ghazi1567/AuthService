using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace AuthService.Web.Security;

/// <summary>Settings of the security headers, section <c>Security</c> (AUTH-271).</summary>
public sealed class SecurityHeadersOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// When true, the script policy is only reported (<c>Content-Security-Policy-Report-Only</c>), not enforced: a browser logs what it would
    /// have blocked and runs it anyway. For a first rollout. The rest of the policy (framing, base address, form targets, plugins) is always
    /// enforced.
    /// </summary>
    public bool ScriptPolicyReportOnly { get; set; }
}

/// <summary>
/// Headers every response carries. Pages are never cached (they can hold tokens in their address or form state), are not framed by
/// other sites, and send no referrer, so a link token cannot leak to another site. Scripts may come only from this site, or be inline with
/// the request's nonce (AUTH-271): an injected <c>&lt;script&gt;</c> or event-handler attribute is blocked by the browser.
/// </summary>
public static class SecurityHeaders
{
    private const string FormTargetKey = "SecurityHeaders.FormTarget";
    private const string NonceKey = "SecurityHeaders.Nonce";

    /// <summary>
    /// Lets one response post a form to one other origin (and no other): used only to hand a product its token (decision 0007). Every other
    /// response keeps <c>form-action 'self'</c>.
    /// </summary>
    public static void AllowFormPostTo(HttpContext context, Uri address) =>
        context.Items[FormTargetKey] = address.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// This request's script nonce: put it on every inline <c>&lt;script&gt;</c> as <c>nonce="…"</c>. A new random value for every response,
    /// so a page an attacker saw cannot be used to guess the next one.
    /// </summary>
    public static string Nonce(HttpContext context)
    {
        if (context.Items.TryGetValue(NonceKey, out var existing) && existing is string nonce)
        {
            return nonce;
        }

        // URL-safe base64 (allowed in a CSP nonce): nothing in it needs escaping in an HTML attribute.
        nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        context.Items[NonceKey] = nonce;
        return nonce;
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var nonce = Nonce(context);
        context.Response.OnStarting(() =>
        {
            var reportOnly = context.RequestServices.GetService<IOptions<SecurityHeadersOptions>>()?.Value.ScriptPolicyReportOnly == true;
            var headers = context.Response.Headers;
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";

            // Blazor's own scripts and app.js are files of this site; the only inline scripts (the import map and the product hand-off)
            // carry the nonce. No 'unsafe-inline' and no 'unsafe-eval'.
            var scripts = $"script-src 'self' 'nonce-{nonce}'";
            var formTarget = context.Items.TryGetValue(FormTargetKey, out var target) && target is string origin ? $" {origin}" : "";
            var always = $"frame-ancestors 'none'; base-uri 'self'; form-action 'self'{formTarget}; object-src 'none'";
            if (reportOnly)
            {
                headers["Content-Security-Policy"] = always;
                headers["Content-Security-Policy-Report-Only"] = scripts;
            }
            else
            {
                headers["Content-Security-Policy"] = $"{always}; {scripts}";
            }

            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

            if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        await next();
    });
}
