using System.Net;
using System.Text;
using AuthService.Web.Api;
using Microsoft.AspNetCore.Antiforgery;

namespace AuthService.Web.Security;

/// <summary>
/// Opening a product from the picker (decision 0007). The person's token for that one product is handed over with a form post to the
/// address the platform admin set for it: in the request body, never in an address, never logged, never sent as a referrer.
/// </summary>
public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/choose-product/open", OpenAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> OpenAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        SessionService sessions,
        AuthApiClient api)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        var form = await context.Request.ReadFormAsync();
        if (!Guid.TryParse(form["tenantId"].ToString(), out var tenantId) || !Guid.TryParse(form["applicationId"].ToString(), out var applicationId))
        {
            return Results.BadRequest();
        }

        var picker = $"/choose-product?tenantId={tenantId}";
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(picker)}");
        }

        var sessionId = context.User.FindFirst(SessionAuthentication.SessionIdClaim)?.Value;
        var identityToken = sessionId is null ? null : await sessions.GetAccessTokenAsync(sessionId, context.RequestAborted);
        if (identityToken is null)
        {
            return Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(picker)}&reason=expired");
        }

        // Where the product opens, and whether this person may open it, come from the API's own answer, never from the request.
        var mine = await api.GetAllAsync<MyProductItem>($"/api/auth/users/me/applications?tenantId={tenantId}", identityToken, context.RequestAborted);
        if (!mine.IsSuccess)
        {
            return Results.Redirect($"{picker}&stay=1&error={Code(mine.Problem!)}&app={applicationId}");
        }

        var product = mine.Value!.FirstOrDefault(item => item.ApplicationId == applicationId);
        if (product is not { State: ProductState.Available, LaunchUrl: { Length: > 0 } launchUrl } || !Uri.TryCreate(launchUrl, UriKind.Absolute, out var address))
        {
            return Results.Redirect($"{picker}&stay=1&error=not_available&app={applicationId}");
        }

        var exchange = await api.PostAsync<TokenExchangeRequest, TokenExchangeResponse>(
            "/api/auth/token/exchange", new TokenExchangeRequest(tenantId, applicationId), context.RequestAborted, identityToken);
        if (!exchange.IsSuccess)
        {
            var wait = exchange.Problem!.Status == StatusCodes.Status429TooManyRequests
                ? $"&wait={(int)Math.Clamp(Math.Ceiling((exchange.Problem.RetryAfter ?? TimeSpan.FromSeconds(60)).TotalSeconds), 1, 3600)}"
                : "";
            return Results.Redirect($"{picker}&stay=1&error={Code(exchange.Problem)}&app={applicationId}{wait}");
        }

        // This one response may post a form to the product's own address, and nowhere else.
        SecurityHeaders.AllowFormPostTo(context, address);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(HandOffPage(product.Name, address, exchange.Value!, $"{picker}&stay=1"), "text/html", Encoding.UTF8);
    }

    private static string Code(ApiProblem problem) => problem.Status switch
    {
        StatusCodes.Status429TooManyRequests => "rate_limited",
        StatusCodes.Status403Forbidden when problem.Code == "tenant_suspended" => "tenant_suspended",
        StatusCodes.Status403Forbidden => "not_available",
        StatusCodes.Status401Unauthorized => "expired",
        _ => "unavailable",
    };

    /// <summary>
    /// A small page whose only job is to post the token to the product. Every value is HTML-encoded. It posts at once when scripts run
    /// (one inline line, so it needs no other file) and has a Continue button when they don't.
    /// </summary>
    private static string HandOffPage(string productName, Uri address, TokenExchangeResponse token, string back)
    {
        string E(string value) => WebUtility.HtmlEncode(value);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light dark">
            <title>Opening {{E(productName)}} · Auth Service</title>
            <style>
            body{font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;margin:0;padding:72px 16px;display:flex;justify-content:center;background:Canvas;color:CanvasText}
            main{max-width:520px}h1{font-size:24px;line-height:32px;margin:0 0 8px}p{margin:0 0 24px;line-height:24px}
            .btn{display:inline-block;margin-right:8px;padding:8px 16px;border:1px solid currentColor;border-radius:6px;background:transparent;color:inherit;font:inherit;text-decoration:none;cursor:pointer}
            </style>
            </head>
            <body>
            <main id="main">
              <h1>Opening {{E(productName)}}…</h1>
              <p>You're being signed in to {{E(productName)}} with your Auth Service account. If nothing happens, choose Continue.</p>
              <form method="post" action="{{E(address.AbsoluteUri)}}" data-autosubmit>
                <input type="hidden" name="access_token" value="{{E(token.AccessToken)}}">
                <input type="hidden" name="token_type" value="{{E(token.TokenType)}}">
                <input type="hidden" name="expires_in" value="{{token.ExpiresIn}}">
                <button type="submit" class="btn">Continue</button>
                <a class="btn" href="{{E(back)}}">Choose another</a>
              </form>
            </main>
            <script>document.querySelector("[data-autosubmit]").submit();</script>
            </body>
            </html>
            """;
    }
}
