using System.Security.Claims;
using AuthService.Api.Common;

namespace AuthService.Api.Features.Applications;

/// <summary>
/// ONB-04: a platform admin keeps the roles a product ships with and copies them into organisations that subscribed before they existed. The
/// routes are under the admin group, so only a platform admin gets this far.
/// </summary>
public static class RoleTemplateAdminEndpoints
{
    public static RouteGroupBuilder MapRoleTemplatesAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/applications/{applicationId:guid}/role-templates", ListAsync);
        admin.MapPut("/applications/{applicationId:guid}/role-templates", SetAsync);
        admin.MapPost("/applications/{applicationId:guid}/role-templates/apply", ApplyAsync);
        return admin;
    }

    private static async Task<IResult> ListAsync(Guid applicationId, RoleTemplateService templates, CancellationToken cancellationToken)
    {
        var response = await templates.ListAsync(applicationId, cancellationToken);
        return response is null ? ApplicationNotFound() : Results.Ok(response);
    }

    /// <summary>
    /// Sets the product's whole set of role templates. A role grants permissions of the product's own catalogue, by key and level; the
    /// templates not in the request are removed (organisations' copies are not touched), and an empty list removes them all.
    /// </summary>
    private static async Task<IResult> SetAsync(
        Guid applicationId,
        SetRoleTemplatesRequest? request,
        ClaimsPrincipal caller,
        RoleTemplateService templates,
        CancellationToken cancellationToken)
    {
        if (request?.Roles is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(SetRoleTemplatesRequest.Roles)] = ["Roles is required. An empty list removes every role template."],
            });
        }

        if (!RoleTemplateValidator.TryValidate(request.Roles, out var valid, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        var result = await templates.SetAsync(adminId, applicationId, valid, cancellationToken);

        return result switch
        {
            SetRoleTemplatesResult.Stored stored => Results.Ok(stored.Response),
            SetRoleTemplatesResult.InvalidGrants invalid => Results.ValidationProblem(invalid.Errors),
            _ => ApplicationNotFound(),
        };
    }

    /// <summary>Copies the product's role templates into every organisation that has an active subscription to it and lacks a role of that name.</summary>
    private static async Task<IResult> ApplyAsync(
        Guid applicationId,
        ClaimsPrincipal caller,
        RoleTemplateService templates,
        CancellationToken cancellationToken)
    {
        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        var result = await templates.ApplyToSubscribersAsync(adminId, applicationId, cancellationToken);

        return result switch
        {
            ApplyRoleTemplatesResult.Applied applied => Results.Ok(applied.Response),
            ApplyRoleTemplatesResult.Conflict => ApiProblem.Create(StatusCodes.Status409Conflict, "role_name_conflict",
                "A role was created at the same moment", "An organisation created a role with one of these names while they were being copied. Nothing was changed. Try again."),
            _ => ApplicationNotFound(),
        };
    }

    private static IResult ApplicationNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "application_not_found", "Application not found", "No application has this id.");
}
