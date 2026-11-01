using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using AuthService.IntegrationTests.Tok04;

namespace AuthService.IntegrationTests.Rol01;

/// <summary>
/// <c>GET /api/auth/tenants/me/permissions</c>: what a tenant admin building a role can grant. These tests have no ticket: they
/// close a known gap (a role-building screen had no way to list the permissions of the products a tenant uses).
/// </summary>
public sealed class TenantPermissionCatalogTests(PostgresContainerFixture postgres) : IsolationTestBase(postgres)
{
    [Fact]
    public async Task ItListsTheAuthServiceItselfAndEveryProductTheTenantUsesWithTheirPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await PlatformAdminTokenAsync(ct);
        var whatsapp = await ProductAsync(admin, "whatsapp-saas", ct, "campaign.create", "campaign.read");
        var billing = await ProductAsync(admin, "billing", ct, "invoice.send");
        var t = await TenantAsync("Acme Ltd", "boss@acme.com", ct, whatsapp, billing);

        var catalog = await CatalogAsync(t.OwnerToken, ct);

        catalog.Levels.ShouldBe(["ReadOnly", "CreateRead", "Full"]);
        catalog.Applications.Select(application => application.Key).ShouldBe(["auth-service", "billing", "whatsapp-saas"], "the Auth Service first, then by name");
        catalog.Applications[0].IsBuiltIn.ShouldBeTrue();
        catalog.Applications[0].Permissions.Select(permission => permission.Key).ShouldContain("roles.read");
        catalog.Applications[0].Permissions.Select(permission => permission.Key).ShouldContain("members.invite");
        var shop = catalog.Applications.Single(application => application.Key == "whatsapp-saas");
        shop.ApplicationId.ShouldBe(whatsapp.Id);
        shop.Permissions.Select(permission => permission.Key).ShouldBe(["campaign.create", "campaign.read", "thing.use"]);
        shop.Permissions.ShouldAllBe(permission => permission.Label.Length > 0);
        catalog.Applications.Single(application => application.Key == "billing").Permissions.Select(permission => permission.Key).ShouldBe(["invoice.send", "thing.use"]);
    }

    [Fact]
    public async Task AProductTheTenantDoesNotUseIsNotListed()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await PlatformAdminTokenAsync(ct);
        var used = await ProductAsync(admin, "used", ct, "a.thing");
        var unused = await ProductAsync(admin, "unused", ct, "b.thing");
        var t = await TenantAsync("Acme Ltd", "boss@acme.com", ct, used);
        var someoneElse = await TenantAsync("Other Ltd", "boss@other.com", ct, unused);

        var catalog = await CatalogAsync(t.OwnerToken, ct);

        catalog.Applications.Select(application => application.Key).ShouldBe(["auth-service", "used"]);
        someoneElse.Id.ShouldNotBe(t.Id);
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Cancelled")]
    public async Task AProductWhoseSubscriptionIsNotActiveIsNotListed(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await PlatformAdminTokenAsync(ct);
        var product = await ProductAsync(admin, "whatsapp-saas", ct, "campaign.create");
        var t = await TenantAsync("Acme Ltd", "boss@acme.com", ct, product);
        (await CatalogAsync(t.OwnerToken, ct)).Applications.Select(application => application.Key).ShouldContain("whatsapp-saas");

        var changed = await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(t.Id, product.Id), $$"""{"status": "{{status}}"}""", product.ServiceToken, ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CatalogAsync(t.OwnerToken, ct)).Applications.Select(application => application.Key).ShouldBe(["auth-service"]);
    }

    [Fact]
    public async Task EverythingListedCanBeGrantedAndAPermissionAddedLaterAppears()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await PlatformAdminTokenAsync(ct);
        var product = await ProductAsync(admin, "whatsapp-saas", ct, "campaign.create");
        var t = await TenantAsync("Acme Ltd", "boss@acme.com", ct, product);

        var catalog = await CatalogAsync(t.OwnerToken, ct);
        var everything = catalog.Applications
            .SelectMany(application => application.Permissions.Select(permission => new RoleGrantInput(application.ApplicationId, permission.Key, "ReadOnly")))
            .ToArray();
        everything.ShouldNotBeEmpty();
        (await PostAsync(RoleEndpoints.Route, new CreateRoleRequest("Everything", everything), t.OwnerToken, ct)).StatusCode
            .ShouldBe(HttpStatusCode.Created, "every listed permission is accepted by a role");

        (await PostAsync($"/api/auth/admin/applications/{product.Id}/permissions",
            new { permissions = new[] { new { key = "brand.new", label = "Brand new" } } }, admin, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CatalogAsync(t.OwnerToken, ct)).Applications.Single(application => application.Key == "whatsapp-saas")
            .Permissions.Select(permission => permission.Key).ShouldContain("brand.new");
    }

    [Fact]
    public async Task ItNeedsRolesReadAndTheTenantIsTheTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await PlatformAdminTokenAsync(ct);
        var product = await ProductAsync(admin, "whatsapp-saas", ct, "campaign.create");
        var t = await TenantAsync("Acme Ltd", "boss@acme.com", ct, product);
        var reader = await CreateRoleAsync(t.OwnerToken, "Reader", ct, new RoleGrantInput(BuiltInApplication.Id, "roles.read", "ReadOnly"));
        var other = await TenantAsync("Other Ltd", "boss@other.com", ct, product);

        var readerMember = await AddMemberAsync(t.Id, "reader@acme.com", ct);
        await AssignRolesAsync(t.OwnerToken, readerMember.MembershipId, ct, reader);
        var plainMember = await AddMemberAsync(t.Id, "plain@acme.com", ct);

        var readerToken = await TenantTokenAsync(await IdentityTokenAsync("reader@acme.com", ct), t.Id, ct);
        var plainToken = await TenantTokenAsync(await IdentityTokenAsync("plain@acme.com", ct), t.Id, ct);
        (await GetAsync(TenantPermissionCatalogEndpoints.Route, readerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(TenantPermissionCatalogEndpoints.Route, plainToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(TenantPermissionCatalogEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(TenantPermissionCatalogEndpoints.Route, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(TenantPermissionCatalogEndpoints.Route, await IdentityTokenAsync("boss@acme.com", ct), ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Another tenant's admin sees their own tenant's products only, never this one's role names or members.
        var theirs = await CatalogAsync(other.OwnerToken, ct);
        theirs.Applications.Select(application => application.Key).ShouldBe(["auth-service", "whatsapp-saas"]);
        plainMember.MembershipId.ShouldNotBe(Guid.Empty);
    }

    private async Task<TenantPermissionCatalog> CatalogAsync(string tenantToken, CancellationToken ct)
    {
        var response = await GetAsync(TenantPermissionCatalogEndpoints.Route, tenantToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TenantPermissionCatalog>(Json, ct))!;
    }
}
