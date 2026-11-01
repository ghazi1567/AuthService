using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Authorization;

/// <summary>
/// Finds the endpoints of a product API that are not covered by a permission policy, so a forgotten one fails a test instead of
/// shipping open. Call it from a test that builds the real application, and assert that the result is empty.
/// </summary>
public static class EndpointPolicyAudit
{
    /// <summary>
    /// Lists every endpoint (as <c>METHOD /route</c>) that has no <see cref="PermissionRequirement"/>: anonymous ones, ones with
    /// no authorization at all, and ones that only need a valid token. Pass <paramref name="isExempt"/> to name the few that are meant
    /// to be that way, such as a health check, and set <paramref name="requirePermission"/> to false to let endpoints that only
    /// <c>RequireTenant</c> pass as well.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindEndpointsWithoutPermissionPolicyAsync(
        IServiceProvider services,
        Func<RouteEndpoint, bool>? isExempt = null,
        bool requirePermission = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var unprotected = new List<string>();

        foreach (var endpoint in services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            if (isExempt?.Invoke(endpoint) == true)
            {
                continue;
            }

            var policy = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
                ? null
                : await AuthorizationPolicy.CombineAsync(
                    policyProvider,
                    endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                    endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());

            var requirements = policy?.Requirements ?? [];
            var covered = requirements.Any(requirement => requirement is PermissionRequirement)
                || (!requirePermission && requirements.Any(requirement => requirement is TenantRequirement));

            if (!covered)
            {
                unprotected.Add($"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])} {endpoint.RoutePattern.RawText}");
            }
        }

        return [.. unprotected.Order(StringComparer.Ordinal)];
    }
}
