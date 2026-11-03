using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace AuthService.Api.Features.Invitations;

public static class InvitationEndpoints
{
    public const string InviteRoute = "/api/auth/tenants/me/invitations";

    public const string AcceptRoute = "/api/auth/invitations/accept";

    public static string ResendRouteFor(Guid membershipId) => $"{InviteRoute}/{membershipId}/resend";

    public static IEndpointRouteBuilder MapInvitations(this IEndpointRouteBuilder app)
    {
        app.MapPost(InviteRoute, InviteAsync).RequireTenantPermission(PermissionKeys.MembersInvite, AccessLevel.Full);

        // Who has not accepted yet is part of knowing who is in the tenant, so it takes the same permission as the member list.
        app.MapGet(InviteRoute, ListPendingAsync).RequireTenantPermission(PermissionKeys.MembersRead, AccessLevel.ReadOnly);
        app.MapPost($"{InviteRoute}/{{membershipId:guid}}/resend", ResendAsync).RequireTenantPermission(PermissionKeys.MembersInvite, AccessLevel.Full);

        // Open to anyone holding a link. A new person has no account to log in to; an existing one is checked inside.
        app.MapPost(AcceptRoute, AcceptAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> InviteAsync(
        InviteRequest request, ClaimsPrincipal caller, InviteUserService invitations, CancellationToken cancellationToken)
    {
        if (!InviteValidator.TryValidate(request, out var valid, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        // Who invites and for which tenant come only from the verified token, never from the request.
        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var result = await invitations.InviteAsync(userId, tenantId, valid!, cancellationToken);

        return result switch
        {
            InviteResult.Invited invited => Results.Json(invited.Invitation, statusCode: StatusCodes.Status201Created),

            InviteResult.AlreadyMember => ApiProblem.Create(StatusCodes.Status409Conflict, "already_member", "Already a member",
                "This email already belongs to this tenant."),

            InviteResult.OwnerRoleRestricted => ApiProblem.Create(StatusCodes.Status403Forbidden, "owner_role_restricted", "Owner role restricted",
                "Only an Owner can invite someone as an Owner."),

            InviteResult.ApplicationNotSubscribed => ApiProblem.Create(StatusCodes.Status400BadRequest, "application_not_subscribed",
                "Application not subscribed", "The tenant has no active subscription to one of these applications."),

            InviteResult.NoSeatAvailable full => ApiProblem.Create(StatusCodes.Status409Conflict, "no_seat_available", "No seat available",
                $"There is no free seat in: {string.Join(", ", full.ApplicationKeys)}."),

            _ => ApiProblem.Create(StatusCodes.Status404NotFound, "not_found", "Not found", "A role was not found in this tenant."),
        };
    }

    private static async Task<IResult> ListPendingAsync(
        ClaimsPrincipal caller, ManageInvitationsService invitations, [AsParameters] PageQuery page, CancellationToken cancellationToken)
    {
        if (page.Problem(byName: false) is { } problem)
        {
            return problem;
        }

        // The tenant comes only from the verified token, never from the request.
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        return Results.Ok(await invitations.ListPendingAsync(tenantId, page, cancellationToken));
    }

    private static async Task<IResult> ResendAsync(
        Guid membershipId, ClaimsPrincipal caller, HttpContext httpContext, ManageInvitationsService invitations, CancellationToken cancellationToken)
    {
        var userId = caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id.");
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");
        var result = await invitations.ResendAsync(userId, tenantId, membershipId, cancellationToken);

        switch (result)
        {
            case ResendInvitationResult.Resent resent:
                return Results.Ok(resent.Invitation);

            case ResendInvitationResult.TooSoon tooSoon:
                httpContext.Response.Headers.RetryAfter = tooSoon.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ApiProblem.Create(StatusCodes.Status429TooManyRequests, "resend_too_soon", "Sent a moment ago",
                    "This invitation was emailed a moment ago. Try again shortly.");

            case ResendInvitationResult.NotPending:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "invitation_not_pending", "Not waiting to be accepted",
                    "This person has already accepted, or was not invited.");

            default:
                return ApiProblem.Create(StatusCodes.Status404NotFound, "not_found", "Not found", "No such invitation in this tenant.");
        }
    }

    private static async Task<IResult> AcceptAsync(AcceptInvitationRequest request, HttpContext httpContext, AcceptInvitationService accept, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(AcceptInvitationRequest.Token)] = ["Token is required."] });
        }

        var result = await accept.AcceptAsync(request.Token, request.Password, await LoggedInUserAsync(httpContext), cancellationToken);

        return result switch
        {
            AcceptInvitationResult.Accepted accepted => Results.Ok(accepted.Response),

            AcceptInvitationResult.PasswordInvalid invalid => Results.ValidationProblem(
                new Dictionary<string, string[]> { [nameof(AcceptInvitationRequest.Password)] = [invalid.Reason] }),

            AcceptInvitationResult.PasswordNotAllowed => ApiProblem.Create(StatusCodes.Status400BadRequest, "password_not_allowed",
                "Password not allowed", "You already have an account, so accepting does not set a password. Leave it out."),

            AcceptInvitationResult.LoginRequired => ApiProblem.Create(StatusCodes.Status401Unauthorized, "login_required", "Log in first",
                "This email already has an account. Log in with it, then accept the invitation."),

            AcceptInvitationResult.WrongUser => ApiProblem.Create(StatusCodes.Status403Forbidden, "wrong_account", "Wrong account",
                "This invitation is for a different email address than the account you are logged in as."),

            _ => ApiProblem.Create(StatusCodes.Status410Gone, "invitation_invalid", "Invitation no longer valid",
                "This invitation is unknown, expired, already used or withdrawn."),
        };
    }

    /// <summary>The person logged in with an identity token, if any. A missing, invalid or product token counts as nobody.</summary>
    private static async Task<Guid?> LoggedInUserAsync(HttpContext httpContext)
    {
        var authentication = await httpContext.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!authentication.Succeeded || authentication.Principal.HasClaim(claim => claim.Type == "aud"))
        {
            return null;
        }

        return authentication.Principal.GetUserId();
    }
}
