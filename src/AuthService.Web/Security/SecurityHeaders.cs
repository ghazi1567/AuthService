namespace AuthService.Web.Security;

/// <summary>
/// Headers every response carries. Pages are never cached (they can hold tokens in their address or form state), are not framed by
/// other sites, and send no referrer, so a link token cannot leak to another site. A script-source policy is deliberately not set
/// yet: it is decided with the finished design, because Blazor's own scripts need a nonce or hash policy that must be tested against it.
/// </summary>
public static class SecurityHeaders
{
    private const string FormTargetKey = "SecurityHeaders.FormTarget";

    /// <summary>
    /// Lets one response post a form to one other origin (and no other): used only to hand a product its token (decision 0007). Every other
    /// response keeps <c>form-action 'self'</c>.
    /// </summary>
    public static void AllowFormPostTo(HttpContext context, Uri address) =>
        context.Items[FormTargetKey] = address.GetLeftPart(UriPartial.Authority);

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            var formTarget = context.Items.TryGetValue(FormTargetKey, out var target) && target is string origin ? $" {origin}" : "";
            headers["Content-Security-Policy"] = $"frame-ancestors 'none'; base-uri 'self'; form-action 'self'{formTarget}; object-src 'none'";
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
