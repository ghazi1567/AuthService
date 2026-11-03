using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.ApiKeys;

public static class ApiKeyEndpoints
{
    public const string Route = "/api/auth/apikeys";

    public const string ValidateRoute = "/api/auth/apikeys/validate";

    public static string KeyRouteFor(Guid keyId) => $"{Route}/{keyId}";

    public static string RotateRouteFor(Guid keyId) => $"{Route}/{keyId}/rotate";

    public static IEndpointRouteBuilder MapApiKeys(this IEndpointRouteBuilder app)
    {
        // A tenant's own people manage its keys, with a tenant-admin token.
        app.MapPost(Route, CreateAsync).RequireTenantPermission(PermissionKeys.ApiKeysManage, AccessLevel.Full);
        app.MapGet(Route, ListAsync).RequireTenantPermission(PermissionKeys.ApiKeysRead, AccessLevel.ReadOnly);
        app.MapGet($"{Route}/{{keyId:guid}}", GetAsync).RequireTenantPermission(PermissionKeys.ApiKeysRead, AccessLevel.ReadOnly);
        app.MapDelete($"{Route}/{{keyId:guid}}", RevokeAsync).RequireTenantPermission(PermissionKeys.ApiKeysManage, AccessLevel.Full);
        app.MapPost($"{Route}/{{keyId:guid}}/rotate", RotateAsync).RequireTenantPermission(PermissionKeys.ApiKeysManage, AccessLevel.Full);

        // A product asks what a presented key is, with its service token.
        app.MapPost(ValidateRoute, ValidateAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateApiKeyRequest request, ClaimsPrincipal caller, ApiKeyService keys, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!ApiKeyValidator.TryValidate(request, out var valid, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        // Who and which tenant come only from the verified token, never from the request.
        var (userId, tenantId) = Caller(caller);
        var result = await keys.CreateAsync(userId, tenantId, valid!, cancellationToken);

        // The response carries the raw key, shown once: it must never be stored by a browser or proxy.
        httpContext.Response.Headers.CacheControl = "no-store";

        return result switch
        {
            CreateApiKeyResult.Created created => Results.Json(created.Response, statusCode: StatusCodes.Status201Created),
            CreateApiKeyResult.InvalidScope invalid => Results.ValidationProblem(invalid.Errors),

            CreateApiKeyResult.ExceedsCreatorsPermissions beyond => ApiProblem.Create(StatusCodes.Status403Forbidden, "scope_exceeds_permissions",
                "Scope too wide", $"A key cannot have more than you do. You do not hold: {string.Join(", ", beyond.Permissions)}."),

            _ => throw new InvalidOperationException("Unhandled create result."),
        };
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal caller, ApiKeyService keys, [AsParameters] PageQuery page, CancellationToken cancellationToken) =>
        page.Problem(byName: false) ?? Results.Ok(await keys.ListAsync(Caller(caller).TenantId, page, cancellationToken));

    private static async Task<IResult> GetAsync(Guid keyId, ClaimsPrincipal caller, ApiKeyService keys, CancellationToken cancellationToken) =>
        await keys.GetAsync(Caller(caller).TenantId, keyId, cancellationToken) is { } key ? Results.Ok(key) : KeyNotFound();

    private static async Task<IResult> RevokeAsync(Guid keyId, ClaimsPrincipal caller, ApiKeyService keys, CancellationToken cancellationToken)
    {
        var (userId, tenantId) = Caller(caller);
        return await keys.RevokeAsync(userId, tenantId, keyId, cancellationToken) ? Results.NoContent() : KeyNotFound();
    }

    private static async Task<IResult> RotateAsync(
        Guid keyId, RotateApiKeyRequest? request, ClaimsPrincipal caller, ApiKeyService keys, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (request?.GraceHours is < 0 or > ApiKeyValidator.MaxGraceHours)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(RotateApiKeyRequest.GraceHours)] = [$"GraceHours must be between 0 and {ApiKeyValidator.MaxGraceHours}."],
            });
        }

        var (userId, tenantId) = Caller(caller);
        var result = await keys.RotateAsync(userId, tenantId, keyId, request?.GraceHours, cancellationToken);

        // The response carries the new raw key, shown once.
        httpContext.Response.Headers.CacheControl = "no-store";

        return result switch
        {
            RotateApiKeyResult.Rotated rotated => Results.Json(
                new { rotated.Response.Key, rotated.Response.RawKey, previousKeyExpiresAt = rotated.PreviousKeyExpiresAt },
                statusCode: StatusCodes.Status201Created),

            RotateApiKeyResult.NotActive => ApiProblem.Create(StatusCodes.Status409Conflict, "api_key_not_active", "API key not active",
                "A revoked or expired key cannot be rotated. Create a new one."),

            _ => KeyNotFound(),
        };
    }

    private static async Task<IResult> ValidateAsync(
        ValidateApiKeyRequest request, ClaimsPrincipal caller, ApiKeyValidationService validation, HttpContext httpContext, CancellationToken cancellationToken)
    {
        AccessLevel? level = null;
        if (request.Level is not null)
        {
            if (!Enum.TryParse<AccessLevel>(request.Level.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)
                || request.Level.Trim().All(character => char.IsDigit(character) || character == '-'))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(ValidateApiKeyRequest.Level)] = ["Level must be None, ReadOnly, CreateRead or Full."],
                });
            }

            level = parsed;
        }

        // Which product is asking comes only from its verified service token.
        var applicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        var result = await validation.ValidateAsync(applicationId, request.Key, request.Permission, level, cancellationToken);
        httpContext.Response.Headers.CacheControl = "no-store";

        return result switch
        {
            ValidateApiKeyResult.Valid valid => Results.Ok(valid.Key),

            ValidateApiKeyResult.Forbidden forbidden => ApiProblem.Create(StatusCodes.Status403Forbidden, forbidden.Code, "Not allowed", forbidden.Detail),

            // Never anonymous, never tenant-less: the product must refuse the call.
            _ => ApiProblem.Create(StatusCodes.Status401Unauthorized, "invalid_api_key", "Invalid API key", "The API key is not valid."),
        };
    }

    private static (Guid UserId, Guid TenantId) Caller(ClaimsPrincipal caller) =>
        (caller.GetUserId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a user id."),
         caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id."));

    // 404, never 403, for a key of another tenant, so its existence is not confirmed.
    private static IResult KeyNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "not_found", "Not found", "No such API key in this tenant.");
}
