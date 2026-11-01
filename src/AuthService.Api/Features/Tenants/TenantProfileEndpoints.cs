using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Tenants;

public sealed record TenantProfileResponse(Guid Id, string Name, string Status, DateTimeOffset CreatedAt);

/// <summary>TEN-05: the profile of the tenant the caller is working in.</summary>
public static class TenantProfileEndpoints
{
    public const string Route = "/api/auth/tenant/me";

    public static IEndpointRouteBuilder MapTenantProfile(this IEndpointRouteBuilder app)
    {
        // Any valid tenant-admin token: seeing your own tenant's name needs no permission beyond belonging to it.
        app.MapGet(Route, GetAsync).RequireTenantContext();
        return app;
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal caller, AuthDbContext db, CancellationToken cancellationToken)
    {
        // The tenant is the token's `tid` and nothing else: a tenant id in the query string, path or body is never read.
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");

        var tenant = await db.Tenants.AsNoTracking()
            .Where(candidate => candidate.Id == tenantId)
            .Select(candidate => new { candidate.Id, candidate.Name, candidate.Status, candidate.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken);

        return tenant is null
            ? ApiProblem.Create(StatusCodes.Status404NotFound, "tenant_not_found", "Tenant not found", "No such tenant.")
            : Results.Ok(new TenantProfileResponse(tenant.Id, tenant.Name, tenant.Status.ToString(), tenant.CreatedAt));
    }
}
