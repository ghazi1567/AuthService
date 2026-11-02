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
        app.MapPost("/platform/signing-keys/rotate", RotateSigningKeyAsync).AllowAnonymous();
        app.MapPost("/platform/products/{applicationId:guid}/rotate-secret", RotateProductSecretAsync).AllowAnonymous();
        app.MapPost("/platform/products/{applicationId:guid}/active", SetProductActiveAsync).AllowAnonymous();
        return app;
    }

    /// <summary>The word the admin must type before an emergency rotation goes ahead.</summary>
    public const string RotationWord = "ROTATE";

    private static async Task<IResult> SetProductActiveAsync(
        Guid applicationId,
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

        const string page = "/platform/products";
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(page)}");
        }

        var opened = await platform.LoadAsync(context);
        if (opened.Outcome != PlatformOutcome.Allowed)
        {
            return Results.Redirect(page);
        }

        var form = await context.Request.ReadFormAsync();
        if (!bool.TryParse(form["isActive"].ToString(), out var isActive))
        {
            return Results.BadRequest();
        }

        var result = await api.PutNoContentAsync($"/api/auth/admin/applications/{applicationId}/active", new SetProductActiveRequest(isActive), opened.Token!, context.RequestAborted);
        if (result.IsSuccess)
        {
            return Results.Redirect($"{page}?notice={(isActive ? "available" : "withdrawn")}&app={applicationId}");
        }

        var code = result.Problem!.Code is "no_permissions_registered" or "application_not_found"
            ? result.Problem.Code
            : result.Problem.Status == StatusCodes.Status403Forbidden ? "forbidden" : "unavailable";
        return Results.Redirect($"{page}?error={code}&app={applicationId}");
    }

    private static async Task<IResult> RotateProductSecretAsync(
        Guid applicationId,
        HttpContext context,
        IAntiforgery antiforgery,
        PlatformSession platform,
        AuthApiClient api,
        OneTimeSecrets secrets)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        const string page = "/platform/products";
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(page)}");
        }

        var opened = await platform.LoadAsync(context);
        if (opened.Outcome != PlatformOutcome.Allowed)
        {
            return Results.Redirect(page);
        }

        var form = await context.Request.ReadFormAsync();
        if (!int.TryParse(form["graceHours"].ToString(), out var grace) || grace is < 0 or > 720)
        {
            return Results.Redirect($"{page}?error=bad_grace&app={applicationId}");
        }

        var result = await api.PostAsync<RotateSecretRequest, RotatedSecret>(
            $"/api/auth/admin/applications/{applicationId}/secret/rotate", new RotateSecretRequest(grace), context.RequestAborted, opened.Token);
        if (result.IsSuccess && context.User.FindFirst(SessionAuthentication.SessionIdClaim) is { } sid)
        {
            // The new secret waits on the server; the address carries only a reference, the (public) client id and when the old secret stops.
            var reference = secrets.Put(sid.Value, result.Value!.ClientSecret);
            var oldEnds = grace == 0 ? "now" : result.Value.PreviousSecretsExpireAt.UtcDateTime.ToString("O");
            return Results.Redirect($"{page}?reveal={reference}&app={applicationId}&client={Uri.EscapeDataString(result.Value.ClientId)}&oldEnds={Uri.EscapeDataString(oldEnds)}");
        }

        var code = result.Problem?.Code == "application_not_found" ? "application_not_found"
            : result.Problem?.Status == StatusCodes.Status403Forbidden ? "forbidden"
            : result.Problem?.Status == StatusCodes.Status400BadRequest ? "bad_grace"
            : "unavailable";
        return Results.Redirect($"{page}?error={code}&app={applicationId}");
    }

    private static async Task<IResult> RotateSigningKeyAsync(
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

        const string page = "/platform/signing-keys";
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(page)}");
        }

        var opened = await platform.LoadAsync(context);
        if (opened.Outcome != PlatformOutcome.Allowed)
        {
            return Results.Redirect(page);
        }

        // Typed by hand, so a slip or a stray click can't set it off. The API is not asked unless the word matches.
        var form = await context.Request.ReadFormAsync();
        if (!string.Equals(form["confirm"].ToString().Trim(), RotationWord, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Redirect($"{page}?rotate=1&error=confirm");
        }

        var result = await api.PostAsync<object, RotatedSigningKey>("/api/auth/admin/signing-keys/rotate", new { }, context.RequestAborted, opened.Token);
        if (result.IsSuccess)
        {
            return Results.Redirect($"{page}?notice=rotated&kid={Uri.EscapeDataString(result.Value!.KeyId)}");
        }

        return Results.Redirect($"{page}?error={(result.Problem!.Status == StatusCodes.Status403Forbidden ? "forbidden" : "unavailable")}");
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
