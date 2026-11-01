using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.ApiKeys;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Tok04;

/// <summary>
/// Helpers for the TOK-04 isolation suite: two tenants, two products, and the people who work in them, built through the real API so
/// the tokens under test are the ones a product would really receive.
/// </summary>
public abstract class IsolationTestBase(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    protected const string AdminEmail = "ops@platform.test";

    /// <summary>A tenant with an Owner, subscribed to the given products, and the Owner's tenant-admin token.</summary>
    protected sealed record Tenant(SignupResponse Owner, string OwnerEmail, string OwnerToken)
    {
        public Guid Id => Owner.TenantId;
    }

    protected sealed record Member(Guid UserId, Guid MembershipId, string Email);

    protected async Task<string> PlatformAdminTokenAsync(CancellationToken ct) => (await CreatePlatformAdminAsync(AdminEmail, ct)).AccessToken;

    /// <summary>An active product whose catalog holds the given permissions (and <c>thing.use</c>).</summary>
    protected async Task<TestProduct> ProductAsync(string adminToken, string key, CancellationToken ct, params string[] permissions)
    {
        var product = await CreateActiveProductAsync(adminToken, key, ct);
        if (permissions.Length > 0)
        {
            var catalog = permissions.Select(permission => new { key = permission, label = permission }).ToArray();
            (await PostAsync($"/api/auth/admin/applications/{product.Id}/permissions", new { permissions = catalog }, adminToken, ct))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        return product;
    }

    protected async Task<Tenant> TenantAsync(string name, string ownerEmail, CancellationToken ct, params TestProduct[] subscribeTo)
    {
        var owner = await SignUpVerifiedAsync(name, ownerEmail, ct);
        foreach (var product in subscribeTo)
        {
            (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        return new Tenant(owner, ownerEmail, await TenantTokenAsync(await IdentityTokenAsync(ownerEmail, ct), owner.TenantId, ct));
    }

    protected async Task<string> IdentityTokenAsync(string email, CancellationToken ct) => (await LoginForTokensAsync(email, ct)).AccessToken;

    protected async Task<Guid> CreateRoleAsync(string tenantToken, string name, CancellationToken ct, params RoleGrantInput[] grants)
    {
        var response = await PostAsync(RoleEndpoints.Route, new CreateRoleRequest(name, grants), tenantToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
    }

    protected async Task AssignRolesAsync(string tenantToken, Guid membershipId, CancellationToken ct, params Guid[] roleIds) =>
        (await PutAsync(RoleEndpoints.MemberRolesRouteFor(membershipId), new AssignRolesRequest(roleIds), tenantToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

    /// <summary>A new person with their own account, an Active membership in <paramref name="tenantId"/> and, optionally, a seat.</summary>
    protected async Task<Member> AddMemberAsync(Guid tenantId, string email, CancellationToken ct, TestProduct? seatIn = null)
    {
        var person = await SignUpVerifiedAsync("Home of " + email, email, ct);
        return await JoinAsync(person.UserId, tenantId, email, ct, seatIn);
    }

    /// <summary>Makes an existing person an Active member of another tenant.</summary>
    protected async Task<Member> JoinAsync(Guid userId, Guid tenantId, string email, CancellationToken ct, TestProduct? seatIn = null)
    {
        await AddMembershipAsync(userId, tenantId, MembershipStatus.Active, ct);
        Guid membershipId = default;
        await WithDbAsync(async db =>
        {
            membershipId = (await db.TenantMemberships.SingleAsync(membership => membership.UserId == userId && membership.TenantId == tenantId, ct)).Id;
            if (seatIn is not null)
            {
                db.ApplicationAccess.Add(new MembershipApplicationAccess
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    MembershipId = membershipId,
                    ApplicationId = seatIn.Id,
                    GrantedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
        });
        return new Member(userId, membershipId, email);
    }

    protected async Task<Guid> CreateApiKeyAsync(string tenantToken, string name, TestProduct product, CancellationToken ct)
    {
        var response = await PostAsync(ApiKeyEndpoints.Route, new CreateApiKeyRequest(name, product.Id, [new ApiKeyPermissionInput("thing.use", "ReadOnly")]), tenantToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(Json, ct))!.Key.Id;
    }

    /// <summary>A person's product token for one product in one tenant, as the exchange issues it.</summary>
    protected async Task<string> ProductTokenAsync(string identityToken, Guid tenantId, TestProduct product, CancellationToken ct)
    {
        var response = await PostAsync("/api/auth/token/exchange", new { tenantId, applicationId = product.Id }, identityToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;
    }

    protected static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? bearerToken, CancellationToken ct, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request, ct);
    }
}
