using AuthService.Api.Tokens;

namespace AuthService.Api.Features.Admin;

public static class AdminEndpoints
{
    public const string BasePath = "/api/auth/admin";

    /// <summary>
    /// The one group every platform-operator endpoint is mapped into (APP-04). The platform-admin policy is applied here, to the
    /// whole group, so an endpoint added under <c>/api/auth/admin</c> cannot be left open by forgetting to protect it. A test
    /// also checks that no endpoint under that path escapes the policy.
    /// </summary>
    public static RouteGroupBuilder MapAdminGroup(this IEndpointRouteBuilder app) =>
        app.MapGroup(BasePath).RequireAuthorization(IdentityAuthentication.PlatformAdminPolicyName);
}
