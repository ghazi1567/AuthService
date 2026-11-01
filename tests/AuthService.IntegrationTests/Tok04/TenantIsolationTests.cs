using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.ApiKeys;
using AuthService.Api.Features.Members;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Tenants;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Tok04;

/// <summary>
/// TOK-04 (AUTH-29) the isolation suite: whatever else changes, one tenant can never read or change another's data, and one product's
/// token never works for another. These run in the normal suite on every build, so a regression in any layer fails the build.
/// The Jira test case ids for this story were not reachable when it was built, so the tests are named for the acceptance criteria.
/// </summary>
public sealed class TenantIsolationTests(PostgresContainerFixture postgres) : IsolationTestBase(postgres)
{
    private sealed record World(TestProduct ProductX, TestProduct ProductY, Tenant A, Tenant B, Guid BRole, Member BMember, Guid BKey, Guid ARole);

    [Fact]
    public async Task TOK04_TenantAsTokenReadsNoneOfTenantBsData()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);

        var reads = new[]
        {
            RoleEndpoints.Route, MemberEndpoints.Route, ListSubscriptionsEndpoints.Route, ApiKeyEndpoints.Route,
            TenantAuditEndpoints.Route, TenantProfileEndpoints.Route,
        };
        foreach (var route in reads)
        {
            var response = await GetAsync(route, w.A.OwnerToken, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, route);
            var body = await response.Content.ReadAsStringAsync(ct);

            body.ShouldNotContain(w.B.Id.ToString(), Case.Insensitive, $"{route} must not mention tenant B");
            body.ShouldNotContain(w.BRole.ToString(), Case.Insensitive, route);
            body.ShouldNotContain("Secret B Role", Case.Insensitive, route);
            body.ShouldNotContain(w.BMember.Email, Case.Insensitive, route);
            body.ShouldNotContain(w.B.OwnerEmail, Case.Insensitive, route);
            body.ShouldNotContain("B secret key", Case.Insensitive, route);
            body.ShouldNotContain(w.BKey.ToString(), Case.Insensitive, route);
        }
    }

    [Fact]
    public async Task TOK04_TenantBsOwnReadsStillShowTheirData_SoTheAbsenceAboveIsMeaningful()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);

        (await (await GetAsync(RoleEndpoints.Route, w.B.OwnerToken, ct)).Content.ReadAsStringAsync(ct)).ShouldContain("Secret B Role");
        (await (await GetAsync(MemberEndpoints.Route, w.B.OwnerToken, ct)).Content.ReadAsStringAsync(ct)).ShouldContain(w.BMember.Email);
        (await (await GetAsync(ApiKeyEndpoints.Route, w.B.OwnerToken, ct)).Content.ReadAsStringAsync(ct)).ShouldContain("B secret key");
    }

    [Fact]
    public async Task TOK04_TenantAsTokenCannotAddressTenantBsRecordsAndLeavesThemUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        var token = w.A.OwnerToken;

        var attempts = new Dictionary<string, HttpResponseMessage>
        {
            ["rename B's role"] = await SendAsync(Client, HttpMethod.Patch, RoleEndpoints.RoleRouteFor(w.BRole), token, ct, new UpdateRoleRequest("Hijacked", null)),
            ["delete B's role"] = await SendAsync(Client, HttpMethod.Delete, RoleEndpoints.RoleRouteFor(w.BRole), token, ct),
            ["disable B's member"] = await SendAsync(Client, HttpMethod.Patch, MemberEndpoints.MemberRouteFor(w.BMember.MembershipId), token, ct, new SetMemberStatusRequest("Disabled")),
            ["assign A's role to B's member"] = await PutAsync(RoleEndpoints.MemberRolesRouteFor(w.BMember.MembershipId), new AssignRolesRequest([w.ARole]), token, ct),
            ["give B's member a seat"] = await PutAsync(MemberAccessEndpoints.RouteFor(w.BMember.MembershipId, w.ProductX.Id), new { }, token, ct),
            ["take a seat from B's member"] = await SendAsync(Client, HttpMethod.Delete, MemberAccessEndpoints.RouteFor(w.BMember.MembershipId, w.ProductX.Id), token, ct),
            ["read B's key"] = await GetAsync(ApiKeyEndpoints.KeyRouteFor(w.BKey), token, ct),
            ["revoke B's key"] = await SendAsync(Client, HttpMethod.Delete, ApiKeyEndpoints.KeyRouteFor(w.BKey), token, ct),
            ["rotate B's key"] = await PostAsync(ApiKeyEndpoints.RotateRouteFor(w.BKey), new { }, token, ct),
        };

        foreach (var (what, response) in attempts)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{what}: a record of another tenant is not confirmed to exist");
        }

        await WithDbAsync(async db =>
        {
            (await db.Roles.AsNoTracking().SingleAsync(role => role.Id == w.BRole, ct)).Name.ShouldBe("Secret B Role");
            (await db.TenantMemberships.AsNoTracking().SingleAsync(membership => membership.Id == w.BMember.MembershipId, ct)).Status.ShouldBe(MembershipStatus.Active);
            (await db.MembershipRoles.AsNoTracking().CountAsync(assignment => assignment.MembershipId == w.BMember.MembershipId, ct)).ShouldBe(0);
            (await db.ApplicationAccess.AsNoTracking().CountAsync(access => access.MembershipId == w.BMember.MembershipId, ct)).ShouldBe(0);
            (await db.ApiKeys.AsNoTracking().SingleAsync(key => key.Id == w.BKey, ct)).RevokedAt.ShouldBeNull();
        });
    }

    [Fact]
    public async Task TOK04_ARequestCannotNameAnotherTenantInThePathQueryHeaderOrBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{RoleEndpoints.Route}?tenantId={w.B.Id}")
        {
            Content = JsonContent.Create(new { name = "Smuggled", tenantId = w.B.Id, tenant_id = w.B.Id, permissions = Array.Empty<object>() }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", w.A.OwnerToken);
        request.Headers.Add("X-Tenant-Id", w.B.Id.ToString());
        request.Headers.Add("Tenant-Id", w.B.Id.ToString());

        var created = await Client.SendAsync(request, ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        await WithDbAsync(async db =>
        {
            var role = await db.Roles.AsNoTracking().SingleAsync(candidate => candidate.Name == "Smuggled", ct);
            role.TenantId.ShouldBe(w.A.Id, "the tenant is the token's, whatever the request says");
        });

        foreach (var route in new[] { $"{RoleEndpoints.Route}?tenantId={w.B.Id}", $"{MemberEndpoints.Route}?tenantId={w.B.Id}", $"{ApiKeyEndpoints.Route}?tenantId={w.B.Id}" })
        {
            var read = new HttpRequestMessage(HttpMethod.Get, route);
            read.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", w.A.OwnerToken);
            read.Headers.Add("X-Tenant-Id", w.B.Id.ToString());
            var body = await (await Client.SendAsync(read, ct)).Content.ReadAsStringAsync(ct);
            body.ShouldNotContain(w.BMember.Email, Case.Insensitive, route);
            body.ShouldNotContain("Secret B Role", Case.Insensitive, route);
            body.ShouldNotContain("B secret key", Case.Insensitive, route);
        }
    }

    [Fact]
    public async Task TOK04_AProductTokenForOneProductIsRejectedByAnotherProduct()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        var tokenForX = await ProductTokenAsync(await IdentityTokenAsync(w.A.OwnerEmail, ct), w.A.Id, w.ProductX, ct);
        var tokenForY = await ProductTokenAsync(await IdentityTokenAsync(w.A.OwnerEmail, ct), w.A.Id, w.ProductY, ct);
        await using var apiX = await SampleProductApi.StartAsync(Client, "product-x-api");
        await using var apiY = await SampleProductApi.StartAsync(Client, "product-y-api");

        (await SendAsync(apiX.Client, HttpMethod.Get, "/campaigns", tokenForX, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(apiY.Client, HttpMethod.Get, "/campaigns", tokenForY, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(apiY.Client, HttpMethod.Get, "/campaigns", tokenForX, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(apiX.Client, HttpMethod.Get, "/campaigns", tokenForY, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(apiY.Client, HttpMethod.Get, "/me", tokenForX, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TOK04_AProductTokenDoesNotWorkAgainstTheAuthServicesTenantEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        var tokenForX = await ProductTokenAsync(await IdentityTokenAsync(w.A.OwnerEmail, ct), w.A.Id, w.ProductX, ct);

        foreach (var route in new[] { RoleEndpoints.Route, MemberEndpoints.Route, ApiKeyEndpoints.Route, TenantProfileEndpoints.Route })
        {
            var status = (await GetAsync(route, tokenForX, ct)).StatusCode;
            status.ShouldBeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized], route);
        }
    }

    [Fact]
    public async Task TOK04_ATenantAdminTokenDoesNotWorkAtAProduct()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        await using var apiX = await SampleProductApi.StartAsync(Client, "product-x-api");

        (await SendAsync(apiX.Client, HttpMethod.Get, "/campaigns", w.A.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TOK04_APersonInTwoTenantsCannotUseTheTokenForOneToReachTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        // The owner of A is also a plain member of B, with a seat in X but no role that grants anything.
        await JoinAsync(w.A.Owner.UserId, w.B.Id, w.A.OwnerEmail, ct, w.ProductX);
        var identity = await IdentityTokenAsync(w.A.OwnerEmail, ct);
        var adminForA = await TenantTokenAsync(identity, w.A.Id, ct);
        var adminForB = await TenantTokenAsync(identity, w.B.Id, ct);
        var productForA = await ProductTokenAsync(identity, w.A.Id, w.ProductX, ct);
        var productForB = await ProductTokenAsync(identity, w.B.Id, w.ProductX, ct);
        await using var apiX = await SampleProductApi.StartAsync(Client, "product-x-api");

        // The token for A is A's, in the Auth Service and at the product, even when it is told otherwise.
        var members = await (await GetAsync($"{MemberEndpoints.Route}?tenantId={w.B.Id}", adminForA, ct)).Content.ReadAsStringAsync(ct);
        members.ShouldContain(w.A.OwnerEmail);
        members.ShouldNotContain(w.BMember.Email, Case.Insensitive, "A's token sees A's people only");
        var atProduct = await SendAsync(apiX.Client, HttpMethod.Get, $"/campaigns?tenantId={w.B.Id}", productForA, ct);
        (await atProduct.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid().ShouldBe(w.A.Id);

        // Being Owner of A gives nothing in B: B's token holds no permission, so B's administration and B's product refuse it.
        (await GetAsync(RoleEndpoints.Route, adminForB, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(MemberEndpoints.Route, adminForB, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(apiX.Client, HttpMethod.Get, "/campaigns", productForB, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(apiX.Client, HttpMethod.Get, "/me", productForB, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "a member of B reaches B's own context, and nothing of A's");

        // And a product token cannot be traded for a token in the other tenant.
        (await PostAsync("/api/auth/token/exchange", new { tenantId = w.B.Id, applicationId = w.ProductX.Id }, productForA, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TOK04_APlatformAdminHasNoTenantAccessJustForBeingOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        var adminIdentity = await IdentityTokenAsync(AdminEmail, ct);

        (await PostAsync("/api/auth/token/exchange", new { tenantId = w.A.Id, applicationId = w.ProductX.Id }, adminIdentity, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreach (var route in new[] { RoleEndpoints.Route, MemberEndpoints.Route, ApiKeyEndpoints.Route, TenantProfileEndpoints.Route })
        {
            (await GetAsync(route, adminIdentity, ct)).StatusCode.ShouldBeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized], route);
        }
    }

    [Fact]
    public async Task TOK04_ATenantsAuditLogOnlyEverHoldsItsOwnEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);

        var response = await GetAsync(TenantAuditEndpoints.Route + "?limit=200", w.A.OwnerToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("role", Case.Insensitive, "tenant A did create a role, so its log is not empty");
        body.ShouldNotContain("Secret B Role", Case.Insensitive);
        body.ShouldNotContain(w.BRole.ToString(), Case.Insensitive);
    }

    /// <summary>Two products and two tenants subscribed to both; B holds a role, a member and a key that A must never see.</summary>
    private async Task<World> WorldAsync(CancellationToken ct)
    {
        var adminToken = await PlatformAdminTokenAsync(ct);
        var productX = await ProductAsync(adminToken, "product-x", ct, "campaign.read", "campaign.create");
        var productY = await ProductAsync(adminToken, "product-y", ct, "campaign.read", "campaign.create");
        var a = await TenantAsync("Tenant A", "boss@a.com", ct, productX, productY);
        var b = await TenantAsync("Tenant B", "boss@b.com", ct, productX, productY);

        var bRole = await CreateRoleAsync(b.OwnerToken, "Secret B Role", ct, new RoleGrantInput(productX.Id, "campaign.read", "ReadOnly"));
        var bMember = await AddMemberAsync(b.Id, "bmember@b.com", ct);
        var bKey = await CreateApiKeyAsync(b.OwnerToken, "B secret key", productX, ct);
        var aRole = await CreateRoleAsync(a.OwnerToken, "A Role", ct);
        return new World(productX, productY, a, b, bRole, bMember, bKey, aRole);
    }
}
