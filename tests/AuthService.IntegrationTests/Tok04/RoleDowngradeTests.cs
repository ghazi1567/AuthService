using System.Net;
using AuthService.Api.Features.Roles;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.IntegrationTests.Tok04;

/// <summary>
/// TOK-04 (AUTH-29): lowering someone's role takes effect in the next token they get, and the token they already hold stops working
/// when it expires. Time is moved on a clock shared by the Auth Service and the product API instead of waiting 15 minutes.
/// </summary>
public sealed class RoleDowngradeTests(PostgresContainerFixture postgres) : IsolationTestBase(postgres)
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    protected override HarnessOptions Options => new()
    {
        ConfigureServices = services => services.AddSingleton<TimeProvider>(_clock),
    };

    [Fact]
    public async Task TOK04_ADowngradedRoleBlocksTheActionOnceTheOldTokenExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var adminToken = await PlatformAdminTokenAsync(ct);
        var product = await ProductAsync(adminToken, "whatsapp-saas", ct, "campaign.read", "campaign.create");
        var tenant = await TenantAsync("Acme Ltd", "boss@acme.com", ct, product);
        var role = await CreateRoleAsync(tenant.OwnerToken, "Campaign manager", ct, new RoleGrantInput(product.Id, "campaign.create", "Full"));
        var member = await AddMemberAsync(tenant.Id, "worker@acme.com", ct, product);
        await AssignRolesAsync(tenant.OwnerToken, member.MembershipId, ct, role);
        var identity = await IdentityTokenAsync(member.Email, ct);
        await using var api = await SampleProductApi.StartAsync(Client, "whatsapp-saas-api", timeProvider: _clock);

        var before = await ProductTokenAsync(identity, tenant.Id, product, ct);
        (await SendAsync(api.Client, HttpMethod.Post, "/campaigns", before, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the role allows it");

        var downgrade = await SendAsync(Client, HttpMethod.Patch, RoleEndpoints.RoleRouteFor(role), tenant.OwnerToken, ct,
            new UpdateRoleRequest(null, [new RoleGrantInput(product.Id, "campaign.create", "ReadOnly")]));
        downgrade.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Ten minutes on, the next token carries the lower level and the product refuses the action with it...
        _clock.Advance(TimeSpan.FromMinutes(10));
        var after = await ProductTokenAsync(identity, tenant.Id, product, ct);
        (await SendAsync(api.Client, HttpMethod.Post, "/campaigns", after, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the new token is ReadOnly");

        // ...while the old token, issued before the change, still works until it runs out: that is the documented bound.
        (await SendAsync(api.Client, HttpMethod.Post, "/campaigns", before, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "still inside its 15 minutes");

        // Once it expires the old token is refused everywhere, and nothing is left that can do the action.
        _clock.Advance(TimeSpan.FromMinutes(6));
        (await SendAsync(api.Client, HttpMethod.Post, "/campaigns", before, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(api.Client, HttpMethod.Post, "/campaigns", after, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(api.Client, HttpMethod.Get, "/campaigns", after, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "campaign.read was never granted to this role");
    }

    [Fact]
    public async Task TOK04_RemovingTheRoleAltogetherLeavesTheNextTokenWithNoPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        var adminToken = await PlatformAdminTokenAsync(ct);
        var product = await ProductAsync(adminToken, "whatsapp-saas", ct, "campaign.read", "campaign.create");
        var tenant = await TenantAsync("Acme Ltd", "boss@acme.com", ct, product);
        var role = await CreateRoleAsync(tenant.OwnerToken, "Campaign manager", ct, new RoleGrantInput(product.Id, "campaign.create", "Full"));
        var spare = await CreateRoleAsync(tenant.OwnerToken, "Spare", ct);
        var member = await AddMemberAsync(tenant.Id, "worker@acme.com", ct, product);
        await AssignRolesAsync(tenant.OwnerToken, member.MembershipId, ct, role);
        var identity = await IdentityTokenAsync(member.Email, ct);
        await using var api = await SampleProductApi.StartAsync(Client, "whatsapp-saas-api", timeProvider: _clock);
        var before = await ProductTokenAsync(identity, tenant.Id, product, ct);

        await AssignRolesAsync(tenant.OwnerToken, member.MembershipId, ct, spare);

        var after = await ProductTokenAsync(identity, tenant.Id, product, ct);
        (await SendAsync(api.Client, HttpMethod.Post, "/campaigns", after, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(api.Client, HttpMethod.Post, "/campaigns", before, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "an issued token keeps its permissions until it expires");
    }

    [Fact]
    public async Task TOK04_ARevokedSeatMeansNoNewTokenEvenThoughTheOldOneLivesOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var adminToken = await PlatformAdminTokenAsync(ct);
        var product = await ProductAsync(adminToken, "whatsapp-saas", ct, "campaign.read", "campaign.create");
        var tenant = await TenantAsync("Acme Ltd", "boss@acme.com", ct, product);
        var role = await CreateRoleAsync(tenant.OwnerToken, "Campaign manager", ct, new RoleGrantInput(product.Id, "campaign.create", "Full"));
        var member = await AddMemberAsync(tenant.Id, "worker@acme.com", ct, product);
        await AssignRolesAsync(tenant.OwnerToken, member.MembershipId, ct, role);
        var identity = await IdentityTokenAsync(member.Email, ct);
        await using var api = await SampleProductApi.StartAsync(Client, "whatsapp-saas-api", timeProvider: _clock);
        var before = await ProductTokenAsync(identity, tenant.Id, product, ct);

        (await SendAsync(Client, HttpMethod.Delete, AuthService.Api.Features.Subscriptions.MemberAccessEndpoints.RouteFor(member.MembershipId, product.Id), tenant.OwnerToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await PostAsync("/api/auth/token/exchange", new { tenantId = tenant.Id, applicationId = product.Id }, identity, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(api.Client, HttpMethod.Post, "/campaigns", before, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _clock.Advance(TimeSpan.FromMinutes(16));
        (await SendAsync(api.Client, HttpMethod.Post, "/campaigns", before, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
