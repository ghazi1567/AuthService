using AuthService.Web.Api;
using Microsoft.AspNetCore.Antiforgery;

namespace AuthService.Web.Security;

/// <summary>
/// The platform console's form posts that change something. Like the organisation console's, each is a plain POST with an anti-forgery
/// token that calls the API as the signed-in person and redirects back with a short code in the address (never a name or a token). The
/// API is asked first whether the person may be here at all, so a refused person changes nothing.
/// </summary>
public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/platform/webhooks/{webhookId:guid}/resend", ResendWebhookAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> ResendWebhookAsync(
        Guid webhookId,
        HttpContext context,
        IAntiforgery antiforgery,
        PlatformSession platform,
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

        const string page = "/platform/webhooks";
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(page)}");
        }

        var opened = await platform.LoadAsync(context);
        if (opened.Outcome != PlatformOutcome.Allowed)
        {
            // Not allowed or the sign-in ended: the page itself says which, and nothing was changed.
            return Results.Redirect(page);
        }

        var result = await api.PostAsync<object, WebhookItem>($"/api/auth/admin/webhooks/{webhookId}/resend", new { }, context.RequestAborted, opened.Token);
        if (result.IsSuccess)
        {
            return Results.Redirect($"{page}?notice=resent&m={webhookId}");
        }

        var code = result.Problem!.Code is "webhook_not_failed" or "webhook_not_found"
            ? result.Problem.Code
            : result.Problem.Status == StatusCodes.Status403Forbidden ? "forbidden" : "unavailable";
        return Results.Redirect($"{page}?error={code}&m={webhookId}");
    }
}
