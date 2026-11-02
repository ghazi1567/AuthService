using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Applications;

/// <summary>
/// A product as the platform admin's list shows it (APP-06): enough to see where it stands (not yet active, withdrawn, working) and
/// nothing a product keeps private. No client secret, audience, client id or webhook address.
/// </summary>
public sealed record AdminApplicationItem(Guid Id, string Key, string Name, bool IsActive, int PermissionCount, DateTimeOffset CreatedAt);

public static class ApplicationAdminEndpoints
{
    public const string Route = "/api/auth/admin/applications";

    public static RouteGroupBuilder MapApplicationsAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/applications", ListAsync);
        admin.MapPost("/applications", RegisterAsync);
        admin.MapPost("/applications/{applicationId:guid}/permissions", RegisterPermissionsAsync);
        admin.MapPut("/applications/{applicationId:guid}/active", SetActiveAsync);
        admin.MapPost("/applications/{applicationId:guid}/secret/rotate", RotateSecretAsync);
        return admin;
    }

    /// <summary>
    /// A platform admin lists every product, active or not (APP-06), by name. Unlike the list every signed-in person may read, this
    /// one includes a product that is registered but has no catalog yet and one that was withdrawn, so the admin can finish setting
    /// it up or bring it back. The Auth Service's own built-in product is not a product to manage and is left out.
    /// </summary>
    private static async Task<IResult> ListAsync(AuthDbContext db, CancellationToken cancellationToken)
    {
        var applications = await db.Applications.AsNoTracking()
            .Where(application => !application.IsBuiltIn)
            .OrderBy(application => application.Name).ThenBy(application => application.Key)
            .Select(application => new AdminApplicationItem(
                application.Id,
                application.Key,
                application.Name,
                application.IsActive,
                db.Permissions.Count(permission => permission.ApplicationId == application.Id),
                application.CreatedAt))
            .ToListAsync(cancellationToken);

        return Results.Ok(applications);
    }

    /// <summary>
    /// A platform admin registers a product (APP-01). It is under the admin group, so only a platform admin gets this far.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        RegisterApplicationRequest request,
        ClaimsPrincipal caller,
        RegisterApplicationService register,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!ApplicationValidator.TryValidate(request, out var valid, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        var result = await register.RegisterAsync(adminId, valid!, cancellationToken);

        // The response carries the client secret, shown once: it must never be stored by a browser or proxy.
        httpContext.Response.Headers.CacheControl = "no-store";

        return result switch
        {
            RegisterApplicationResult.Created created => Results.Json(created.Response, statusCode: StatusCodes.Status201Created),

            RegisterApplicationResult.AudienceTaken => ApiProblem.Create(StatusCodes.Status409Conflict, "audience_taken",
                "JWT audience already registered", "Another application already uses this JWT audience."),

            _ => ApiProblem.Create(StatusCodes.Status409Conflict, "application_key_taken",
                "Application key already registered", "An application with this key is already registered."),
        };
    }

    /// <summary>
    /// A platform admin registers or updates a product's permission catalog (APP-02). Re-registering a key updates it in place,
    /// so existing grants of it are untouched.
    /// </summary>
    private static async Task<IResult> RegisterPermissionsAsync(
        Guid applicationId,
        RegisterPermissionsRequest request,
        ClaimsPrincipal caller,
        PermissionCatalogService catalog,
        CancellationToken cancellationToken)
    {
        if (!PermissionCatalogValidator.TryValidate(request, out var valid, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        var result = await catalog.RegisterAsync(adminId, applicationId, valid, cancellationToken);

        return result switch
        {
            // 201 when the request added permissions, 200 when it only updated or repeated ones already there.
            RegisterPermissionsResult.Registered registered => Results.Json(registered.Response,
                statusCode: registered.Response.Created > 0 ? StatusCodes.Status201Created : StatusCodes.Status200OK),

            _ => ApplicationNotFound(),
        };
    }

    /// <summary>A platform admin makes a product available to subscribe to, or withdraws it (APP-02).</summary>
    private static async Task<IResult> SetActiveAsync(
        Guid applicationId,
        SetApplicationActiveRequest request,
        ClaimsPrincipal caller,
        PermissionCatalogService catalog,
        CancellationToken cancellationToken)
    {
        if (request.IsActive is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(SetApplicationActiveRequest.IsActive)] = ["IsActive is required."],
            });
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        var result = await catalog.SetActiveAsync(adminId, applicationId, request.IsActive.Value, cancellationToken);

        return result switch
        {
            SetApplicationActiveResult.Updated updated => Results.Ok(updated.Status),

            SetApplicationActiveResult.NoPermissions => ApiProblem.Create(StatusCodes.Status409Conflict, "no_permissions_registered",
                "No permissions registered", "Register at least one permission for this application before activating it."),

            _ => ApplicationNotFound(),
        };
    }

    /// <summary>
    /// A platform admin replaces a product's client secret (APP-05). The old secret keeps working for a grace period (24 hours by
    /// default, or <c>graceHours</c> in the body; 0 cuts it off at once, for a leaked secret).
    /// </summary>
    private static async Task<IResult> RotateSecretAsync(
        Guid applicationId,
        RotateSecretRequest? request,
        ClaimsPrincipal caller,
        SecretRotationService rotation,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request?.GraceHours is < 0 or > SecretRotationService.MaxGraceHours)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(RotateSecretRequest.GraceHours)] = [$"GraceHours must be between 0 and {SecretRotationService.MaxGraceHours}."],
            });
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        var result = await rotation.RotateAsync(adminId, applicationId, request?.GraceHours, cancellationToken);

        // The response carries the new client secret, shown once: it must never be stored by a browser or proxy.
        httpContext.Response.Headers.CacheControl = "no-store";

        return result switch
        {
            RotateSecretResult.Rotated rotated => Results.Ok(rotated.Response),
            _ => ApplicationNotFound(),
        };
    }

    private static IResult ApplicationNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "application_not_found", "Application not found", "No application has this id.");
}
