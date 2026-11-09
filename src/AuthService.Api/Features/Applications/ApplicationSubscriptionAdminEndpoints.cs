using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.Accounts;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Tenants;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Applications;

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/subscriptions</c>. The seat limit is optional: left out, seats are unlimited.</summary>
public sealed record AdminSubscribeRequest(Guid? TenantId, int? MaxSeats);

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/test-organisations</c>: a new organisation for trying a product, owned by an existing person.</summary>
public sealed record CreateTestOrganisationRequest(string? Name, string? OwnerEmail, int? MaxSeats);

/// <summary>The organisation that was created and its subscription to the product.</summary>
public sealed record TestOrganisationResponse(Guid TenantId, string TenantName, string OwnerEmail, SubscriptionResponse Subscription);

/// <summary>
/// ONB-02 (AUTH-273): a platform admin brings an organisation onto a product from the console, which until now only the product itself could
/// do with its service token. Both routes are under the admin group, so only a platform admin gets this far, and the audit log names the
/// admin as the actor.
/// </summary>
public static class ApplicationSubscriptionAdminEndpoints
{
    public static RouteGroupBuilder MapApplicationSubscriptionsAdmin(this RouteGroupBuilder admin)
    {
        admin.MapPost("/applications/{applicationId:guid}/subscriptions", SubscribeAsync);
        admin.MapPost("/applications/{applicationId:guid}/test-organisations", CreateTestOrganisationAsync);
        return admin;
    }

    /// <summary>
    /// A platform admin subscribes an existing organisation to a product, or makes a suspended or cancelled subscription Active again. The
    /// same rules and the same ApplicationSubscribed event as when the product does it, so the product hears about it either way.
    /// </summary>
    private static async Task<IResult> SubscribeAsync(
        Guid applicationId,
        AdminSubscribeRequest? request,
        ClaimsPrincipal caller,
        AuthDbContext db,
        SubscribeTenantService subscriptions,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request?.TenantId is null || request.TenantId == Guid.Empty)
        {
            errors[nameof(AdminSubscribeRequest.TenantId)] = ["TenantId is required."];
        }

        if (request?.MaxSeats is < 1)
        {
            errors[nameof(AdminSubscribeRequest.MaxSeats)] = ["MaxSeats must be at least 1, or left out for unlimited seats."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!await ProductExistsAsync(db, applicationId, cancellationToken))
        {
            return ApplicationNotFound();
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        var result = await subscriptions.SubscribeAsync(applicationId, request!.TenantId!.Value, request.MaxSeats, cancellationToken, AuditActor.User(adminId));

        return result switch
        {
            SubscribeResult.Subscribed { IsNew: true } created => Results.Json(created.Subscription, statusCode: StatusCodes.Status201Created),
            SubscribeResult.Subscribed reactivated => Results.Ok(reactivated.Subscription),
            SubscribeResult.AlreadySubscribed existing => Results.Ok(existing.Subscription),
            SubscribeResult.ApplicationNotActive => NotActive(),
            SubscribeResult.TenantSuspended => TenantSuspended(),
            _ => ApiProblem.Create(StatusCodes.Status404NotFound, "tenant_not_found", "Organisation not found", "No organisation has this id."),
        };
    }

    /// <summary>
    /// A platform admin creates an organisation to try a product in, owned by someone who already has a verified account, and subscribes it
    /// at once. The product must already be available, and that is checked before anything is created.
    /// </summary>
    private static async Task<IResult> CreateTestOrganisationAsync(
        Guid applicationId,
        CreateTestOrganisationRequest? request,
        ClaimsPrincipal caller,
        AuthDbContext db,
        AuditLog audit,
        TimeProvider timeProvider,
        SubscribeTenantService subscriptions,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request?.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors[nameof(CreateTestOrganisationRequest.Name)] = ["Name is required."];
        }
        else if (name.Length > SignupValidator.MaxTenantNameLength)
        {
            errors[nameof(CreateTestOrganisationRequest.Name)] = [$"Name must be at most {SignupValidator.MaxTenantNameLength} characters."];
        }

        var email = string.IsNullOrWhiteSpace(request?.OwnerEmail) ? null : EmailNormalizer.Normalize(request.OwnerEmail);
        if (email is null)
        {
            errors[nameof(CreateTestOrganisationRequest.OwnerEmail)] = ["OwnerEmail is required."];
        }

        if (request?.MaxSeats is < 1)
        {
            errors[nameof(CreateTestOrganisationRequest.MaxSeats)] = ["MaxSeats must be at least 1, or left out for unlimited seats."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var application = await db.Applications.AsNoTracking()
            .Where(candidate => candidate.Id == applicationId && !candidate.IsBuiltIn)
            .Select(candidate => new { candidate.IsActive })
            .SingleOrDefaultAsync(cancellationToken);
        if (application is null)
        {
            return ApplicationNotFound();
        }

        if (!application.IsActive)
        {
            return NotActive();
        }

        var owner = await db.Users.AsNoTracking()
            .Where(user => user.Email == email)
            .Select(user => new { user.Id, user.Email, user.EmailVerifiedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "owner_not_found", "Owner not found",
                "No account has this email. Ask them to sign up first, then create the organisation.");
        }

        if (owner.EmailVerifiedAt is null)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "owner_email_not_verified", "Owner's email not verified",
                "This person hasn't verified their email yet, so they can't sign in. Ask them to verify it first.");
        }

        var memberships = await db.TenantMemberships.Where(membership => membership.UserId == owner.Id)
            .Select(membership => membership.Status).ToListAsync(cancellationToken);
        if (AccountAccess.IsDisabled(memberships))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "owner_disabled", "Owner's account is disabled",
                "This person's access has been removed, so they can't own a new organisation.");
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        var provisioned = db.AddTenantWithOwner(owner.Id, name!, timeProvider.GetUtcNow());
        audit.Record(AuditActions.TenantCreated, AuditActor.User(adminId), provisioned.Tenant.Id);
        await db.SaveChangesAsync(cancellationToken);

        var result = await subscriptions.SubscribeAsync(applicationId, provisioned.Tenant.Id, request!.MaxSeats, cancellationToken, AuditActor.User(adminId));
        return result switch
        {
            SubscribeResult.Subscribed subscribed => Results.Json(
                new TestOrganisationResponse(provisioned.Tenant.Id, provisioned.Tenant.Name, owner.Email, subscribed.Subscription),
                statusCode: StatusCodes.Status201Created),

            // Not reachable after the checks above unless the product was withdrawn in between; the organisation exists and can be subscribed again.
            _ => ApiProblem.Create(StatusCodes.Status409Conflict, "organisation_not_subscribed", "Organisation created but not subscribed",
                "The organisation was created, but the product became unavailable before it could be subscribed. Subscribe it again once the product is available."),
        };
    }

    private static Task<bool> ProductExistsAsync(AuthDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        db.Applications.AsNoTracking().AnyAsync(application => application.Id == applicationId && !application.IsBuiltIn, cancellationToken);

    private static IResult ApplicationNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "application_not_found", "Application not found", "No application has this id.");

    private static IResult NotActive() =>
        ApiProblem.Create(StatusCodes.Status400BadRequest, "application_not_active", "Application not active",
            "This application is not active, so organisations cannot subscribe to it. Make it available first.");

    private static IResult TenantSuspended() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, "tenant_suspended", "Organisation suspended", "A suspended organisation cannot subscribe to an application.");
}
