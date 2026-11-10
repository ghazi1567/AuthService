using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Entitlements;

/// <summary>
/// ENT-01: a product's limit definitions. A platform admin adds and changes them; the product reads its own with its service token.
/// Both reads are paged by key like every other list (<c>{items,next}</c>).
/// </summary>
public static class LimitDefinitionEndpoints
{
    /// <summary>The product's own limits, read with its service token.</summary>
    public const string CurrentApplicationRoute = "/api/auth/applications/me/limits";

    public static RouteGroupBuilder MapLimitDefinitionsAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/applications/{applicationId:guid}/limits", ListAsync);
        admin.MapPost("/applications/{applicationId:guid}/limits", RegisterAsync);
        return admin;
    }

    public static IEndpointRouteBuilder MapCurrentApplicationLimits(this IEndpointRouteBuilder app)
    {
        app.MapGet(CurrentApplicationRoute, ListOwnAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        return app;
    }

    /// <summary>
    /// A platform admin adds limits to a product or changes them. Like permissions, a key already there is updated in place and never
    /// removed; a field left out (description, unit) is cleared, so send the whole limit each time. 201 when something was added.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        Guid applicationId,
        RegisterLimitsRequest request,
        ClaimsPrincipal caller,
        LimitDefinitionService limits,
        CancellationToken cancellationToken)
    {
        if (!LimitDefinitionValidator.TryValidate(request, out var valid, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        var result = await limits.RegisterAsync(adminId, applicationId, valid, cancellationToken);

        return result switch
        {
            RegisterLimitsResult.Registered registered => Results.Json(registered.Response,
                statusCode: registered.Response.Created > 0 ? StatusCodes.Status201Created : StatusCodes.Status200OK),

            RegisterLimitsResult.TooMany tooMany => ApiProblem.Create(StatusCodes.Status409Conflict, "too_many_limits",
                "Too many limits", $"A product can have at most {LimitDefinitionValidator.MaxLimitsPerProduct} limits; this would make {tooMany.Total}."),

            _ => ApplicationNotFound(),
        };
    }

    /// <summary>A platform admin reads a product's limits by key, active or not. An unknown product and the built-in one are not found.</summary>
    private static async Task<IResult> ListAsync(Guid applicationId, AuthDbContext db, [AsParameters] PageQuery page, CancellationToken cancellationToken)
    {
        if (page.Problem(byName: true) is { } problem)
        {
            return problem;
        }

        var exists = await db.Applications.AsNoTracking().AnyAsync(application => application.Id == applicationId && !application.IsBuiltIn, cancellationToken);
        if (!exists)
        {
            return ApplicationNotFound();
        }

        return Results.Ok(await PageAsync(db, applicationId, page, cancellationToken));
    }

    /// <summary>A product reads its own limits. Which product it is comes only from the verified token, so it can never read another's.</summary>
    private static async Task<IResult> ListOwnAsync(ClaimsPrincipal caller, AuthDbContext db, [AsParameters] PageQuery page, CancellationToken cancellationToken)
    {
        if (page.Problem(byName: true) is { } problem)
        {
            return problem;
        }

        var applicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        return Results.Ok(await PageAsync(db, applicationId, page, cancellationToken));
    }

    private static async Task<PagedResponse<LimitDefinitionDto, NameCursor>> PageAsync(
        AuthDbContext db, Guid applicationId, PageQuery page, CancellationToken cancellationToken)
    {
        var rows = await page.ByNameAsync(
            db.LimitDefinitions.AsNoTracking().Where(limit => limit.ApplicationId == applicationId),
            limit => limit.Key,
            limit => limit.Id,
            cancellationToken);

        return rows.Map(items => items.Select(LimitDefinitionService.ToDto).ToList());
    }

    private static IResult ApplicationNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "application_not_found", "Application not found", "No application has this id.");
}
