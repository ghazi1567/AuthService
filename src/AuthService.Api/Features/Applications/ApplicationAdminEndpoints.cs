using System.Security.Claims;
using AuthService.Api.Common;

namespace AuthService.Api.Features.Applications;

public static class ApplicationAdminEndpoints
{
    public const string Route = "/api/auth/admin/applications";

    public static RouteGroupBuilder MapApplicationsAdmin(this RouteGroupBuilder admin)
    {
        admin.MapPost("/applications", RegisterAsync);
        return admin;
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
}
