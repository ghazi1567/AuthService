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

    /// <summary>The product names in "...there is no free seat in: a, b." so the notice can name them. Nothing else from the message is shown.</summary>
    private static string SeatProducts(string detail)
    {
        var start = detail.IndexOf(" in: ", StringComparison.Ordinal);
        return start < 0 ? "" : detail[(start + 5)..].TrimEnd('.');
    }
}
