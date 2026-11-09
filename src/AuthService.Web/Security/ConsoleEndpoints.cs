using AuthService.Web.Api;
using Microsoft.AspNetCore.Antiforgery;

namespace AuthService.Web.Security;

/// <summary>
/// The console's form posts that change something. Each is a plain POST with an anti-forgery token that calls the API as the signed-in
/// person and then redirects back to the page with a short notice or error code in the address (never a name or a token), so a reload
/// does not send the change twice.
/// </summary>
public static class ConsoleEndpoints
{
    public static IEndpointRouteBuilder MapConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/console/{tenantId:guid}/members/{membershipId:guid}/status", SetMemberStatusAsync).AllowAnonymous();
        app.MapPost("/console/{tenantId:guid}/invitations/{membershipId:guid}/resend", ResendInvitationAsync).AllowAnonymous();
        app.MapPost("/console/{tenantId:guid}/invitations/{membershipId:guid}/revoke", RevokeInvitationAsync).AllowAnonymous();
        app.MapPost("/console/{tenantId:guid}/roles/{roleId:guid}/delete", DeleteRoleAsync).AllowAnonymous();
        app.MapPost("/console/{tenantId:guid}/access/{applicationId:guid}/grant", GrantAccessAsync).AllowAnonymous();
        app.MapPost("/console/{tenantId:guid}/api-keys/{keyId:guid}/rotate", RotateApiKeyAsync).AllowAnonymous();
        app.MapPost("/console/{tenantId:guid}/api-keys/{keyId:guid}/revoke", RevokeApiKeyAsync).AllowAnonymous();
        app.MapPost("/console/{tenantId:guid}/access/{applicationId:guid}/{membershipId:guid}/revoke", RevokeAccessAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> SetMemberStatusAsync(
        Guid tenantId,
        Guid membershipId,
        HttpContext context,
        IAntiforgery antiforgery,
        ConsoleSession console,
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

        var members = $"/console/{tenantId}/members";
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(members)}");
        }

        var form = await context.Request.ReadFormAsync();
        var status = form["status"].ToString();
        if (status is not ("Active" or "Disabled"))
        {
            return Results.BadRequest();
        }

        var opened = await console.LoadAsync(context, tenantId);
        if (opened.Outcome != ConsoleOutcome.Ready)
        {
            return Results.Redirect(members);
        }

        var result = await api.PatchAsync<SetMemberStatusRequest, MemberItem>(
            $"/api/auth/tenants/me/members/{membershipId}", new SetMemberStatusRequest(status), opened.Console!.Token, context.RequestAborted);
        if (result.IsSuccess)
        {
            return Results.Redirect($"{members}?notice={(status == "Active" ? "enabled" : "disabled")}&m={membershipId}");
        }

        var problem = result.Problem!;
        if (problem.Status == StatusCodes.Status401Unauthorized)
        {
            // The token was turned down (its permissions changed, or it expired early): the next page trades for a fresh one.
            console.Refresh(context, tenantId);
            return Results.Redirect(members);
        }

        var known = problem.Code is "owner_role_restricted" or "last_owner" or "membership_invited" or "no_seat_available" or "not_found";
        var code = known ? problem.Code : problem.Status == StatusCodes.Status403Forbidden ? "forbidden" : "unavailable";
        var seats = problem.Code == "no_seat_available" && problem.Detail is { } detail ? $"&seats={Uri.EscapeDataString(SeatProducts(detail))}" : "";
        return Results.Redirect($"{members}?error={code}&m={membershipId}{seats}");
    }

    private static async Task<IResult> ResendInvitationAsync(
        Guid tenantId,
        Guid membershipId,
        HttpContext context,
        IAntiforgery antiforgery,
        ConsoleSession console,
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

        var page = $"/console/{tenantId}/invitations";
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(page)}");
        }

        var opened = await console.LoadAsync(context, tenantId);
        if (opened.Outcome != ConsoleOutcome.Ready)
        {
            return Results.Redirect(page);
        }

        var result = await api.PostNoContentAsync(
            $"/api/auth/tenants/me/invitations/{membershipId}/resend", new { }, context.RequestAborted, opened.Console!.Token);
        if (result.IsSuccess)
        {
            return Results.Redirect($"{page}?notice=resent&m={membershipId}");
        }

        var problem = result.Problem!;
        if (problem.Status == StatusCodes.Status401Unauthorized)
        {
            console.Refresh(context, tenantId);
            return Results.Redirect(page);
        }

        if (problem.Code == "resend_too_soon")
        {
            var wait = problem.RetryAfter is { } retryAfter ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : 60;
            return Results.Redirect($"{page}?error=resend_too_soon&m={membershipId}&wait={wait}");
        }

        var known = problem.Code is "invitation_not_pending" or "not_found" or "no_seat_available";
        var code = known ? problem.Code : problem.Status == StatusCodes.Status403Forbidden ? "forbidden" : "unavailable";
        return Results.Redirect($"{page}?error={code}&m={membershipId}");
    }

    private static async Task<IResult> RevokeInvitationAsync(
        Guid tenantId,
        Guid membershipId,
        HttpContext context,
        IAntiforgery antiforgery,
        ConsoleSession console,
        AuthApiClient api)
    {
        if (await ReadCheckedFormAsync(context, antiforgery) is null)
        {
            return Results.BadRequest();
        }

        var page = $"/console/{tenantId}/invitations";
        var opened = await OpenAsync(context, console, tenantId, page);
        if (opened.Redirect is not null)
        {
            return opened.Redirect;
        }

        var result = await api.DeleteAsync($"/api/auth/tenants/me/invitations/{membershipId}", opened.Console!.Token, context.RequestAborted);
        if (result.IsSuccess)
        {
            return Results.Redirect($"{page}?notice=revoked");
        }

        var problem = result.Problem!;
        if (problem.Status == StatusCodes.Status401Unauthorized)
        {
            console.Refresh(context, tenantId);
            return Results.Redirect(page);
        }

        var known = problem.Code is "invitation_not_pending" or "not_found";
        var code = known ? problem.Code : problem.Status == StatusCodes.Status403Forbidden ? "forbidden" : "unavailable";
        return Results.Redirect($"{page}?error={code}&m={membershipId}");
    }

    private static async Task<IResult> DeleteRoleAsync(
        Guid tenantId,
        Guid roleId,
        HttpContext context,
        IAntiforgery antiforgery,
        ConsoleSession console,
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

        var page = $"/console/{tenantId}/roles";
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(page)}");
        }

        var opened = await console.LoadAsync(context, tenantId);
        if (opened.Outcome != ConsoleOutcome.Ready)
        {
            return Results.Redirect(page);
        }

        var result = await api.DeleteAsync($"/api/auth/roles/{roleId}", opened.Console!.Token, context.RequestAborted);
        if (result.IsSuccess)
        {
            return Results.Redirect($"{page}?notice=deleted");
        }

        var problem = result.Problem!;
        if (problem.Status == StatusCodes.Status401Unauthorized)
        {
            console.Refresh(context, tenantId);
            return Results.Redirect(page);
        }

        var known = problem.Code is "role_in_use" or "owner_role_immutable" or "not_found";
        var code = known ? problem.Code : problem.Status == StatusCodes.Status403Forbidden ? "forbidden" : "unavailable";
        return Results.Redirect($"{page}?error={code}&m={roleId}");
    }

    private static async Task<IResult> GrantAccessAsync(
        Guid tenantId,
        Guid applicationId,
        HttpContext context,
        IAntiforgery antiforgery,
        ConsoleSession console,
        AuthApiClient api)
    {
        var form = await ReadCheckedFormAsync(context, antiforgery);
        if (form is null)
        {
            return Results.BadRequest();
        }

        var page = $"/console/{tenantId}/access";
        if (!Guid.TryParse(form["membershipId"].ToString(), out var membershipId))
        {
            return Results.Redirect($"{page}?error=choose_member&a={applicationId}");
        }

        var opened = await OpenAsync(context, console, tenantId, page);
        if (opened.Redirect is not null)
        {
            return opened.Redirect;
        }

        var result = await api.PutNoContentAsync(
            $"/api/auth/tenants/me/members/{membershipId}/applications/{applicationId}", new { }, opened.Console!.Token, context.RequestAborted);
        return Finish(result, console, context, tenantId, page, "granted", membershipId, applicationId);
    }

    private static async Task<IResult> RevokeAccessAsync(
        Guid tenantId,
        Guid applicationId,
        Guid membershipId,
        HttpContext context,
        IAntiforgery antiforgery,
        ConsoleSession console,
        AuthApiClient api)
    {
        if (await ReadCheckedFormAsync(context, antiforgery) is null)
        {
            return Results.BadRequest();
        }

        var page = $"/console/{tenantId}/access";
        var opened = await OpenAsync(context, console, tenantId, page);
        if (opened.Redirect is not null)
        {
            return opened.Redirect;
        }

        var result = await api.DeleteAsync(
            $"/api/auth/tenants/me/members/{membershipId}/applications/{applicationId}", opened.Console!.Token, context.RequestAborted);
        return Finish(result, console, context, tenantId, page, "revoked", membershipId, applicationId);
    }

    private static async Task<IResult> RotateApiKeyAsync(
        Guid tenantId,
        Guid keyId,
        HttpContext context,
        IAntiforgery antiforgery,
        ConsoleSession console,
        AuthApiClient api,
        OneTimeSecrets secrets)
    {
        var form = await ReadCheckedFormAsync(context, antiforgery);
        if (form is null)
        {
            return Results.BadRequest();
        }

        var page = $"/console/{tenantId}/api-keys";
        var opened = await OpenAsync(context, console, tenantId, page);
        if (opened.Redirect is not null)
        {
            return opened.Redirect;
        }

        if (!int.TryParse(form["graceHours"].ToString(), out var grace) || grace is < 0 or > 720)
        {
            return Results.Redirect($"{page}?error=bad_grace&m={keyId}");
        }

        var result = await api.PostAsync<RotateApiKeyRequest, RotatedApiKey>(
            $"/api/auth/apikeys/{keyId}/rotate", new RotateApiKeyRequest(grace), context.RequestAborted, opened.Console!.Token);
        if (result.IsSuccess && context.User.FindFirst(SessionAuthentication.SessionIdClaim) is { } sid)
        {
            // The new key's value waits on the server; the address carries only a reference, how long the old key lives, and no secret.
            var reference = secrets.Put(sid.Value, result.Value!.RawKey);
            var oldEnds = grace == 0 ? "now" : result.Value.PreviousKeyExpiresAt?.UtcDateTime.ToString("O") ?? "";
            return Results.Redirect($"{page}?reveal={reference}&rotated=1&oldEnds={Uri.EscapeDataString(oldEnds)}");
        }

        return KeyOutcome(result.Problem, console, context, tenantId, page, keyId);
    }

    private static async Task<IResult> RevokeApiKeyAsync(
        Guid tenantId,
        Guid keyId,
        HttpContext context,
        IAntiforgery antiforgery,
        ConsoleSession console,
        AuthApiClient api)
    {
        if (await ReadCheckedFormAsync(context, antiforgery) is null)
        {
            return Results.BadRequest();
        }

        var page = $"/console/{tenantId}/api-keys";
        var opened = await OpenAsync(context, console, tenantId, page);
        if (opened.Redirect is not null)
        {
            return opened.Redirect;
        }

        var result = await api.DeleteAsync($"/api/auth/apikeys/{keyId}", opened.Console!.Token, context.RequestAborted);
        return result.IsSuccess
            ? Results.Redirect($"{page}?notice=revoked&m={keyId}")
            : KeyOutcome(result.Problem, console, context, tenantId, page, keyId);
    }

    private static IResult KeyOutcome(ApiProblem? problem, ConsoleSession console, HttpContext context, Guid tenantId, string page, Guid keyId)
    {
        if (problem?.Status == StatusCodes.Status401Unauthorized)
        {
            console.Refresh(context, tenantId);
            return Results.Redirect(page);
        }

        var known = problem?.Code is "api_key_not_active" or "not_found";
        var code = known ? problem!.Code : problem?.Status == StatusCodes.Status403Forbidden ? "forbidden" : problem?.Status == StatusCodes.Status400BadRequest ? "bad_grace" : "unavailable";
        return Results.Redirect($"{page}?error={code}&m={keyId}");
    }

    /// <summary>The posted form once its anti-forgery token checks out, or null when it does not.</summary>
    private static async Task<IFormCollection?> ReadCheckedFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return null;
        }

        return await context.Request.ReadFormAsync();
    }

    /// <summary>Opens the console for a post, or says where to send the person instead (sign in, or back to the page).</summary>
    private static async Task<(ConsoleContext? Console, IResult? Redirect)> OpenAsync(HttpContext context, ConsoleSession console, Guid tenantId, string page)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return (null, Results.Redirect($"{SessionAuthentication.LoginPath}?returnUrl={Uri.EscapeDataString(page)}"));
        }

        var opened = await console.LoadAsync(context, tenantId);
        return opened.Outcome == ConsoleOutcome.Ready ? (opened.Console, null) : (null, Results.Redirect(page));
    }

    /// <summary>Redirects back with a notice for success, or an error code the page explains.</summary>
    private static IResult Finish(
        ApiResult<bool> result, ConsoleSession console, HttpContext context, Guid tenantId, string page, string notice, Guid membershipId, Guid applicationId)
    {
        if (result.IsSuccess)
        {
            return Results.Redirect($"{page}?notice={notice}&m={membershipId}&a={applicationId}");
        }

        var problem = result.Problem!;
        if (problem.Status == StatusCodes.Status401Unauthorized)
        {
            console.Refresh(context, tenantId);
            return Results.Redirect(page);
        }

        var known = problem.Code is "no_seat_available" or "membership_disabled" or "application_not_subscribed" or "not_found";
        var code = known ? problem.Code : problem.Status == StatusCodes.Status403Forbidden ? "forbidden" : "unavailable";
        return Results.Redirect($"{page}?error={code}&m={membershipId}&a={applicationId}");
    }

    /// <summary>The product names in "...there is no free seat in: a, b." so the notice can name them. Nothing else from the message is shown.</summary>
    private static string SeatProducts(string detail)
    {
        var start = detail.IndexOf(" in: ", StringComparison.Ordinal);
        return start < 0 ? "" : detail[(start + 5)..].TrimEnd('.');
    }
}
