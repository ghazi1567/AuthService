using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Tok04;

/// <summary>
/// TOK-04 (AUTH-29): an inventory of every endpoint the Auth Service exposes. Each one must either be on the short, reviewed list of
/// anonymous endpoints or carry an explicit authorization rule, and every tenant-scoped one must take its tenant from the token and
/// check it. A new endpoint added without a decision fails here until someone makes one.
/// </summary>
public sealed class EndpointInventoryTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    /// <summary>The only endpoints open to anyone. Adding one means adding a line here, in review.</summary>
    private static readonly string[] AnonymousEndpoints =
    [
        "GET /",
        "GET /.well-known/auth-service-configuration",
        "GET /.well-known/jwks.json",
        "GET /health/live",
        "GET /health/ready",
        "GET /metrics",
        "POST /api/auth/email/verify",
        "POST /api/auth/email/verify/resend",
        "POST /api/auth/invitations/accept",
        "POST /api/auth/login",
        "POST /api/auth/logout",
        "POST /api/auth/password/forgot",
        "POST /api/auth/password/reset",
        "POST /api/auth/refresh",
        "POST /api/auth/signup",
        "POST /api/auth/token",
    ];

    private enum Kind
    {
        Anonymous,
        Identity,
        Service,
        PlatformAdmin,
        TenantContext,
        TenantPermission,
        Unprotected,
    }

    [Fact]
    public async Task TOK04_EveryEndpointIsEitherOnTheAnonymousListOrExplicitlyProtected()
    {
        var inventory = await InventoryAsync();

        var anonymous = inventory.Where(entry => entry.Kind == Kind.Anonymous).Select(entry => entry.Name).Order(StringComparer.Ordinal).ToList();
        anonymous.ShouldBe(AnonymousEndpoints.Order(StringComparer.Ordinal).ToList(), "the anonymous endpoints are exactly the reviewed list");
        inventory.Where(entry => entry.Kind == Kind.Unprotected).Select(entry => entry.Name).ShouldBeEmpty("an endpoint with no rule at all");
    }

    [Fact]
    public async Task TOK04_EveryTenantScopedEndpointNeedsATenantAdminTokenAndTakesItsTenantFromIt()
    {
        var inventory = await InventoryAsync();

        var tenantScoped = inventory.Where(entry => IsTenantScoped(entry.Pattern)).ToList();
        tenantScoped.ShouldNotBeEmpty();
        tenantScoped.Where(entry => entry.Kind is not (Kind.TenantContext or Kind.TenantPermission))
            .Select(entry => $"{entry.Name} is {entry.Kind}")
            .ShouldBeEmpty("a tenant-scoped endpoint must check the tenant-admin token");
        tenantScoped.Where(entry => entry.Pattern.Contains("{tenantId", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Name)
            .ShouldBeEmpty("the tenant of such a request is the token's, never a route value");
    }

    [Fact]
    public async Task TOK04_TenantAdminEndpointsThatChangeSomethingNeedAPermissionNotJustATenant()
    {
        var inventory = await InventoryAsync();

        var weak = inventory
            .Where(entry => entry.Kind == Kind.TenantContext && entry.Methods.Any(method => method != "GET"))
            .Select(entry => entry.Name);

        weak.ShouldBeEmpty("a write that any member of the tenant can make");
    }

    [Fact]
    public async Task TOK04_TheOnlyEndpointsThatTakeATenantFromTheRouteAreTheOnesAProductCallsWithItsServiceToken()
    {
        var inventory = await InventoryAsync();

        var fromRoute = inventory.Where(entry => entry.Pattern.Contains("{tenantId", StringComparison.OrdinalIgnoreCase)).ToList();

        fromRoute.ShouldNotBeEmpty();
        fromRoute.Where(entry => entry.Kind != Kind.Service).Select(entry => $"{entry.Name} is {entry.Kind}").ShouldBeEmpty();
    }

    [Fact]
    public async Task TOK04_ThePlatformAdminEndpointsAreAllUnderTheAdminPrefix_AndNothingElseIsAdminOnly()
    {
        var inventory = await InventoryAsync();

        inventory.Where(entry => entry.Kind == Kind.PlatformAdmin && !entry.Pattern.StartsWith("/api/auth/admin/", StringComparison.Ordinal))
            .Select(entry => entry.Name).ShouldBeEmpty();
        inventory.Where(entry => entry.Pattern.StartsWith("/api/auth/admin/", StringComparison.Ordinal) && entry.Kind != Kind.PlatformAdmin)
            .Select(entry => $"{entry.Name} is {entry.Kind}").ShouldBeEmpty("everything under /admin needs a platform admin");
    }

    private static bool IsTenantScoped(string pattern) =>
        pattern.StartsWith("/api/auth/tenants/me", StringComparison.Ordinal)
        || pattern == "/api/auth/tenant/me"
        || pattern.StartsWith("/api/auth/roles", StringComparison.Ordinal)
        || (pattern.StartsWith("/api/auth/apikeys", StringComparison.Ordinal) && pattern != "/api/auth/apikeys/validate");

    private async Task<List<Entry>> InventoryAsync()
    {
        await using var scope = Harness.CreateScope();
        var services = scope.ServiceProvider;
        var entries = new List<Entry>();

        foreach (var endpoint in services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText!;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.ToArray() ?? ["*"];
            var name = $"{string.Join(",", methods)} {pattern}";
            entries.Add(new Entry(name, pattern, methods, Classify(endpoint)));
        }

        entries.ShouldNotBeEmpty();
        return entries;
    }

    private static Kind Classify(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return Kind.Anonymous;
        }

        var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var policyNames = authorizeData.Select(data => data.Policy).Where(policy => policy is not null).ToList();
        if (policyNames.Contains(IdentityAuthentication.PlatformAdminPolicyName))
        {
            return Kind.PlatformAdmin;
        }

        if (policyNames.Contains(IdentityAuthentication.ServicePolicyName))
        {
            return Kind.Service;
        }

        if (policyNames.Contains(IdentityAuthentication.PolicyName))
        {
            return Kind.Identity;
        }

        var inline = endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>();
        if (inline.Any(policy => policy.Requirements.OfType<PermissionRequirement>().Any()))
        {
            return Kind.TenantPermission;
        }

        // The tenant context: a token for the Auth Service's own product (its audience), naming one tenant and one person.
        var requiresBuiltInAudience = inline.Any(policy => policy.Requirements.OfType<ClaimsAuthorizationRequirement>()
            .Any(requirement => requirement.ClaimType == "aud" && requirement.AllowedValues?.Contains(BuiltInApplication.Audience) == true));
        return requiresBuiltInAudience ? Kind.TenantContext : Kind.Unprotected;
    }

    private sealed record Entry(string Name, string Pattern, string[] Methods, Kind Kind);
}
