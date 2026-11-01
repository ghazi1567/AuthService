using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Tenants;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AuthService.IntegrationTests.Ten05;

/// <summary>
/// TEN-05 (AUTH-12) Get Current Tenant Profile, through the real <c>GET /api/auth/tenant/me</c> with a tenant-admin token: a
/// token from token exchange for the Auth Service's own built-in product.
/// Test method names start with their Test Case ID (AUTH_TC12..AUTH_TC14) per the workbook convention.
/// </summary>
public sealed class TenantProfileTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task AUTH_TC12_CallingGetCurrentTenantWithAValidTokenReturnsOnlyTheCallersOwnTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        var token = await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, acme.TenantId, ct);

        var response = await GetAsync(TenantProfileEndpoints.Route, token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tenant = (await response.Content.ReadFromJsonAsync<TenantProfileResponse>(Json, ct))!;
        (tenant.Id, tenant.Name, tenant.Status).ShouldBe((acme.TenantId, "Acme Ltd", "Active"));
    }

    [Fact]
    public async Task AUTH_TC13_AnotherTenantsIdInTheQueryStringIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        var token = await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, acme.TenantId, ct);

        foreach (var tampered in new[] { $"?tenantId={other.TenantId}", $"?tid={other.TenantId}", $"?id={other.TenantId}&TenantId={other.TenantId}" })
        {
            var response = await GetAsync(TenantProfileEndpoints.Route + tampered, token, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<TenantProfileResponse>(Json, ct))!.Id.ShouldBe(acme.TenantId, $"for {tampered}");
        }
    }

    [Fact]
    public async Task AUTH_TC14_CallingGetCurrentTenantWithNoAuthorizationHeaderReturns401()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await GetAsync(TenantProfileEndpoints.Route, null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("unauthorized");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TEN05_APersonInTwoTenantsSeesTheOneTheirTokenIsFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = await SignUpVerifiedAsync("Home Ltd", OwnerEmail, ct);
        var agency = await SignUpVerifiedAsync("Agency Ltd", "boss@agency.com", ct);
        await AddMembershipAsync(home.UserId, agency.TenantId, MembershipStatus.Active, ct);
        var identity = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;

        var forHome = await GetAsync(TenantProfileEndpoints.Route, await TenantTokenAsync(identity, home.TenantId, ct), ct);
        var forAgency = await GetAsync(TenantProfileEndpoints.Route, await TenantTokenAsync(identity, agency.TenantId, ct), ct);

        (await forHome.Content.ReadFromJsonAsync<TenantProfileResponse>(Json, ct))!.Name.ShouldBe("Home Ltd");
        (await forAgency.Content.ReadFromJsonAsync<TenantProfileResponse>(Json, ct))!.Name.ShouldBe("Agency Ltd");
    }

    [Fact]
    public async Task TEN05_AnyActiveMemberCanGetATenantTokenWithoutASubscriptionOrASeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var member = await SignUpVerifiedAsync("Member Co", "member@other.com", ct);
        await AddMembershipAsync(member.UserId, acme.TenantId, MembershipStatus.Active, ct);

        var token = new JsonWebTokenHandler().ReadJsonWebToken(
            await TenantTokenAsync((await LoginForTokensAsync("member@other.com", ct)).AccessToken, acme.TenantId, ct));

        token.Audiences.ShouldBe([BuiltInApplication.Audience]);
        token.GetClaim("tid").Value.ShouldBe(acme.TenantId.ToString());
        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.CountAsync(ct)).ShouldBe(0, "no subscription row is needed for the built-in product");
            (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(0, "and no seat");
        });
    }

    [Theory]
    [InlineData(MembershipStatus.Invited)]
    [InlineData(MembershipStatus.Disabled)]
    public async Task TEN05_APersonWhoIsNotAnActiveMemberCannotGetATenantToken(MembershipStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var outsider = await SignUpVerifiedAsync("Outsider Co", "outsider@other.com", ct);
        await AddMembershipAsync(outsider.UserId, acme.TenantId, status, ct);

        var response = await PostAsync("/api/auth/token/exchange",
            new { tenantId = acme.TenantId, applicationId = BuiltInApplication.Id },
            (await LoginForTokensAsync("outsider@other.com", ct)).AccessToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TEN05_OnlyATenantTokenOpensTheTenantEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var acme = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        (await PostAsync($"/api/auth/tenants/{acme.TenantId}/subscriptions", new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var identity = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        var productExchange = await PostAsync("/api/auth/token/exchange", new { tenantId = acme.TenantId, applicationId = product.Id }, identity, ct);
        var productToken = (await productExchange.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct)).GetProperty("accessToken").GetString()!;

        (await GetAsync(TenantProfileEndpoints.Route, identity, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an identity token has no tenant");
        (await GetAsync(TenantProfileEndpoints.Route, productToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "another product's token");
        (await GetAsync(TenantProfileEndpoints.Route, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a service token");
        (await GetAsync(TenantProfileEndpoints.Route, "garbage", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TEN05_ATenantTokenIsRefusedOnIdentityAndAdminEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminIdentity) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var admin = await SignUpVerifiedAsync("Admin Co", "second-admin@platform.test", ct);
        var adminTenantToken = await TenantTokenAsync(adminIdentity, (await LoginForTokensAsync("ops@platform.test", ct)).Memberships[0].TenantId, ct);

        // Even a platform admin's tenant token opens nothing outside its tenant: it is not an identity token.
        (await GetAsync("/api/auth/users/me", adminTenantToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(ApplicationAdminEndpoints.Route, new { key = "x", name = "X", jwtAudience = "x-api" }, adminTenantToken, ct)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        _ = admin;
    }

    [Fact]
    public async Task TEN05_TheBuiltInProductIsNotOfferedToTenantsAndCannotBeChangedByAdmins()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);

        var list = await GetAsync(ListApplicationsEndpoints.Route, adminToken, ct);
        (await list.Content.ReadFromJsonAsync<List<ApplicationSummary>>(Json, ct))!.ShouldBeEmpty("it is not a product to subscribe to");

        var route = $"{ApplicationAdminEndpoints.Route}/{BuiltInApplication.Id}";
        (await PostAsync(route + "/permissions", new { permissions = new[] { new { key = "x.y", label = "X" } } }, adminToken, ct)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await PutAsync(route + "/active", new { isActive = false }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(route + "/secret/rotate", new { }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await WithDbAsync(async db => (await db.Applications.AsNoTracking().SingleAsync(application => application.IsBuiltIn, ct)).IsActive.ShouldBeTrue());
    }

    [Fact]
    public async Task TEN05_TheBuiltInProductCannotBeRegisteredOverOrGetAServiceToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);

        var sameKey = await PostAsync(ApplicationAdminEndpoints.Route, new { key = BuiltInApplication.Key, name = "Imposter", jwtAudience = "imposter-api" }, adminToken, ct);
        var sameAudience = await PostAsync(ApplicationAdminEndpoints.Route, new { key = "imposter", name = "Imposter", jwtAudience = BuiltInApplication.Audience }, adminToken, ct);
        var token = await PostAsync("/api/auth/token",
            new { grant_type = "client_credentials", client_id = BuiltInApplication.ClientId, client_secret = "anything" }, null, ct);

        sameKey.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        sameAudience.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        token.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TEN05_StartingTheServiceAgainLeavesTheBuiltInProductAsItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await WithDbReadAsync(ct);

        // Running the same synchronisation again (as every start does) is harmless.
        await using (var scope = Harness.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BuiltInApplicationSync>().SyncAsync(ct);
        }

        (await WithDbReadAsync(ct)).ShouldBe(before);
    }

    private async Task<(Guid Id, string Key, bool Active, int Count)> WithDbReadAsync(CancellationToken ct)
    {
        (Guid, string, bool, int) result = default;
        await WithDbAsync(async db =>
        {
            var application = await db.Applications.AsNoTracking().SingleAsync(candidate => candidate.IsBuiltIn, ct);
            result = (application.Id, application.Key, application.IsActive, await db.Applications.CountAsync(ct));
        });
        return result;
    }
}
