using AuthService.Api.Common;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Tokens;

/// <summary>The organisation a tenant-admin token names must not be suspended, whatever the token still says.</summary>
public sealed class ActiveTenantRequirement : IAuthorizationRequirement;

/// <summary>
/// AUTH-268: a tenant-admin token lives up to 15 minutes and carries no word about the organisation's standing, so a suspension would
/// not reach it until it ran out. This looks the organisation up on every tenant-admin request (one primary-key read), so a suspension
/// stops those calls at once and a reactivation lets them through again with the same token.
/// </summary>
public sealed class ActiveTenantHandler(AuthDbContext db) : AuthorizationHandler<ActiveTenantRequirement>
{
    public const string SuspendedReason = "tenant_suspended";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveTenantRequirement requirement)
    {
        // A token without a tenant fails the policy's own assertion; nothing to look up here.
        if (context.User.GetTenantId() is not { } tenantId)
        {
            return;
        }

        var status = await db.Tenants.AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => (TenantStatus?)tenant.Status)
            .SingleOrDefaultAsync();

        if (status == TenantStatus.Suspended)
        {
            context.Fail(new AuthorizationFailureReason(this, SuspendedReason));
        }
        else if (status is not null)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Answers a request refused only because the organisation is suspended with the usual problem and its code, <c>tenant_suspended</c>,
/// so a client can tell it from a missing permission, which stays an empty 403. The status is <b>401</b>, not 403: the token was good when
/// it was issued and its authority has been withdrawn, so the client must trade for a new one, and that exchange answers
/// <c>403 tenant_suspended</c> and says why. The web console already does exactly this on a 401 (drops its cached token and reloads),
/// so an admin sees "is suspended" instead of a misleading "you don't have permission".
/// </summary>
public sealed class TenantSuspendedResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.Any(reason => reason.Message == ActiveTenantHandler.SuspendedReason) == true)
        {
            context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, "tenant_suspended", "Organisation suspended",
                "This organisation is suspended, so its tokens are not accepted. Ask for a new token to see why, or ask a platform admin to reactivate it.").ExecuteAsync(context);
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
