using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.Signup;
using AuthService.Api.Tokens;

namespace AuthService.Api.Features.Tenants;

public static class CreateTenantEndpoints
{
    public const string Route = "/api/auth/tenants";

    public static IEndpointRouteBuilder MapCreateTenant(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, CreateTenantAsync).RequireAuthorization(IdentityAuthentication.PolicyName);
        return app;
    }

    // The new tenant's Owner is the caller named by the verified token, never anyone named in the request.
    private static async Task<IResult> CreateTenantAsync(
        CreateTenantRequest request,
        ClaimsPrincipal caller,
        CreateTenantService createTenant,
        CancellationToken cancellationToken)
    {
        var tenantName = request.TenantName?.Trim();
        if (string.IsNullOrEmpty(tenantName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(CreateTenantRequest.TenantName)] = ["Tenant name is required."],
            });
        }

        if (tenantName.Length > SignupValidator.MaxTenantNameLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(CreateTenantRequest.TenantName)] = [$"Tenant name must be at most {SignupValidator.MaxTenantNameLength} characters."],
            });
        }

        if (caller.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var result = await createTenant.CreateAsync(userId, tenantName, cancellationToken);

        return result switch
        {
            CreateTenantResult.Created created => Results.Json(created.Response, statusCode: StatusCodes.Status201Created),

            CreateTenantResult.AccountDisabled => ApiProblem.Create(StatusCodes.Status403Forbidden, "account_disabled",
                "Account disabled", "This account has been disabled."),

            _ => Unauthorized(),
        };
    }

    private static IResult Unauthorized() =>
        ApiProblem.Create(StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized", "A valid access token is required.");
}
