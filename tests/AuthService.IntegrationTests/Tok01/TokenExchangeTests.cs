using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.TokenExchange;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.IntegrationTests.Tok01;

/// <summary>
/// TOK-01 (AUTH-26) Token Exchange, through the real <c>POST /api/auth/token/exchange</c> with the identity token of a logged-in
/// person. Grants from custom roles arrive with ROL-01, so the cases that need them are placeholders; the built-in Owner role,
/// which holds Full on a product's whole catalog, is exercised here.
/// Test method names start with their Test Case ID (TOK_TC1..TOK_TC5, TOK_TC15..TOK_TC18) per the workbook convention.
/// </summary>
public sealed class TokenExchangeTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact(Skip = "Enabled once ROL-01 and ROL-05 exist: permissions must match the RolePermission rows of all the caller's roles.")]
    public Task TOK_TC1_ExchangeForASubscribedApplicationEmbedsThePermissionsOfAllTheCallersRolesForThatApplicationOnly() => Task.CompletedTask;

    [Fact]
    public async Task TOK_TC2_ExchangeForAnApplicationWithNoActiveSubscriptionIsRejectedWith403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct, subscribe: false);

        var response = await ExchangeAsync(await IdentityTokenAsync(ct), owner.TenantId, product.Id, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("exchange_denied");
    }

    [Fact]
    public async Task TOK_TC3_TheOwnerGetsFullAccessToEveryPermissionInTheCatalogWithoutAnyGrantRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct, extraPermissions: ["campaign.create", "campaign.send", "report.export"]);

        var response = await ExchangeAsync(await IdentityTokenAsync(ct), owner.TenantId, product.Id, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue("a token response must never be cached");
        var token = (await response.Content.ReadFromJsonAsync<TokenExchangeResponse>(Json, ct))!;
        token.TokenType.ShouldBe("Bearer");
        token.ExpiresIn.ShouldBe(15 * 60, "product tokens live 15 minutes");

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.AccessToken);
        jwt.Audiences.ShouldBe(["whatsapp-saas-api"], "one audience: the product's");
        jwt.GetClaim("tid").Value.ShouldBe(owner.TenantId.ToString(), "one tenant");
        jwt.Subject.ShouldBe(owner.UserId.ToString());
        PermissionsOf(jwt).ShouldBe(
            ["campaign.create:Full", "campaign.send:Full", "report.export:Full", "thing.use:Full"],
            "every permission in the product's catalog, at Full");
        (jwt.ValidTo - jwt.ValidFrom).ShouldBe(TimeSpan.FromMinutes(15));
        await WithDbAsync(async db =>
            (await db.Roles.CountAsync(role => role.TenantId == owner.TenantId, ct)).ShouldBe(1, "the tenant has only its built-in Owner role: no grants were stored"));
    }

    [Fact]
    public async Task TOK_TC4_ATokenForOneApplicationHoldsNoPermissionsOfAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var productX = await CreateActiveProductAsync(adminToken, "product-x", ct);
        var productY = await CreateActiveProductAsync(adminToken, "product-y", ct);
        await AddPermissionsAsync(adminToken, productX.Id, ["x.only"], ct);
        await AddPermissionsAsync(adminToken, productY.Id, ["y.only"], ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(productX, owner.TenantId, ct);
        await SubscribeAsync(productY, owner.TenantId, ct);
        var identity = await IdentityTokenAsync(ct);

        var forX = await ExchangedTokenAsync(identity, owner.TenantId, productX.Id, ct);
        var forY = await ExchangedTokenAsync(identity, owner.TenantId, productY.Id, ct);

        PermissionsOf(forX).ShouldBe(["thing.use:Full", "x.only:Full"]);
        PermissionsOf(forY).ShouldBe(["thing.use:Full", "y.only:Full"]);
        forX.Audiences.ShouldBe(["product-x-api"]);
        forY.Audiences.ShouldBe(["product-y-api"]);
    }

    [Fact(Skip = "Enabled once ROL-03 exists: a token issued after a role's permissions change reflects them; an earlier token is unaffected.")]
    public Task TOK_TC5_ExchangeRightAfterARolesPermissionsChangedReflectsTheChange() => Task.CompletedTask;

    [Fact(Skip = "Enabled once ROL-01 and ROL-05 exist: a member holding roles in two products gets only the requested product's permissions.")]
    public Task TOK_TC15_AMultiRoleTokenHoldsOnlyTheRequestedApplicationsPermissions() => Task.CompletedTask;

    [Fact]
    public async Task TOK_TC16_ExchangeWithoutAnAccessGrantIsRejectedWith403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        await WithDbAsync(db => db.ApplicationAccess.ExecuteDeleteAsync(ct));

        var response = await ExchangeAsync(await IdentityTokenAsync(ct), owner.TenantId, product.Id, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MembershipStatus.Invited)]
    [InlineData(MembershipStatus.Disabled)]
    public async Task TOK_TC17_ExchangeForATenantWithoutAnActiveMembershipIsRejectedWith403(MembershipStatus? status)
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        var outsider = await SignUpVerifiedAsync("Outsider Co", "outsider@other.com", ct);
        if (status is { } membershipStatus)
        {
            await AddMembershipAsync(outsider.UserId, owner.TenantId, membershipStatus, ct);
            // Even with a seat, the membership must be Active.
            await WithDbAsync(async db =>
            {
                var membership = await db.TenantMemberships.SingleAsync(candidate => candidate.UserId == outsider.UserId && candidate.TenantId == owner.TenantId, ct);
                db.ApplicationAccess.Add(new MembershipApplicationAccess
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = owner.TenantId,
                    MembershipId = membership.Id,
                    ApplicationId = product.Id,
                    GrantedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            });
        }

        var response = await ExchangeAsync(await IdentityTokenAsync(ct, "outsider@other.com"), owner.TenantId, product.Id, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TOK_TC18_ThePersonInTwoTenantsGetsATokenForOneOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var tenantA = await SignUpVerifiedAsync("Tenant A", "person@both.com", ct);
        var tenantB = await SignUpVerifiedAsync("Tenant B", "boss@b.com", ct);
        await SubscribeAsync(product, tenantA.TenantId, ct);
        await SubscribeAsync(product, tenantB.TenantId, ct);

        // The person is also a plain member of B, with a seat but no role that grants anything there.
        await AddMembershipAsync(tenantA.UserId, tenantB.TenantId, MembershipStatus.Active, ct);
        await WithDbAsync(async db =>
        {
            var membership = await db.TenantMemberships.SingleAsync(candidate => candidate.UserId == tenantA.UserId && candidate.TenantId == tenantB.TenantId, ct);
            db.ApplicationAccess.Add(new MembershipApplicationAccess
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantB.TenantId,
                MembershipId = membership.Id,
                ApplicationId = product.Id,
                GrantedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        });
        var identity = await IdentityTokenAsync(ct, "person@both.com");

        var forA = await ExchangedTokenAsync(identity, tenantA.TenantId, product.Id, ct);
        var forB = await ExchangedTokenAsync(identity, tenantB.TenantId, product.Id, ct);

        forA.GetClaim("tid").Value.ShouldBe(tenantA.TenantId.ToString());
        PermissionsOf(forA).ShouldBe(["thing.use:Full"], "Owner of A");
        forB.GetClaim("tid").Value.ShouldBe(tenantB.TenantId.ToString());
        PermissionsOf(forB).ShouldBeEmpty("a plain member of B gets nothing from being Owner of A");
    }

    [Fact]
    public async Task SUB_TC5_ExchangeForAJustSuspendedApplicationIsRejectedWith403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        var identity = await IdentityTokenAsync(ct);
        (await ExchangeAsync(identity, owner.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SetSubscriptionStatusAsync(product, owner.TenantId, "Suspended", ct);

        (await ExchangeAsync(identity, owner.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await SetSubscriptionStatusAsync(product, owner.TenantId, "Cancelled", ct);
        (await ExchangeAsync(identity, owner.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SUB_TC6_ATokenIssuedBeforeASuspensionKeepsValidatingUntilItExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        var token = await ExchangedTokenAsync(await IdentityTokenAsync(ct), owner.TenantId, product.Id, ct);

        await SetSubscriptionStatusAsync(product, owner.TenantId, "Suspended", ct);

        // A product validates the token locally (signature, audience, expiry), so it is accepted until it runs out, by design.
        (await ValidateAsLocalProductAsync(token.EncodedToken, "whatsapp-saas-api")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task AUTH_TC27_ProductAccessTokenIssuedBeforeLogoutStaysValidUntilItExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        var login = await LoginForTokensAsync(OwnerEmail, ct);
        var token = await ExchangedTokenAsync(login.AccessToken, owner.TenantId, product.Id, ct);

        (await Client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = login.RefreshToken }, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ValidateAsLocalProductAsync(token.EncodedToken, "whatsapp-saas-api")).IsValid.ShouldBeTrue("logout does not invalidate issued product tokens");
    }

    [Fact]
    public async Task APP_TC11_APlatformAdminCannotGetATokenForATenantTheyAreNotIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        var adminIdentity = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;

        var response = await ExchangeAsync(adminIdentity, owner.TenantId, product.Id, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the flag opens platform operations only, never a tenant's product");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TOK01_APermissionAddedToTheCatalogLaterIsInTheOwnersNextToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        var identity = await IdentityTokenAsync(ct);
        PermissionsOf(await ExchangedTokenAsync(identity, owner.TenantId, product.Id, ct)).ShouldBe(["thing.use:Full"]);

        await AddPermissionsAsync((await LoginForTokensAsync(AdminEmail, ct)).AccessToken, product.Id, ["brand.new"], ct);

        PermissionsOf(await ExchangedTokenAsync(identity, owner.TenantId, product.Id, ct)).ShouldBe(["brand.new:Full", "thing.use:Full"]);
    }

    [Fact]
    public async Task TOK01_ADeactivatedProductAndASuspendedTenantAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        var identity = await IdentityTokenAsync(ct);
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;

        await WithDbAsync(db => db.Tenants.ExecuteUpdateAsync(setters => setters.SetProperty(tenant => tenant.Status, TenantStatus.Suspended), ct));
        (await ExchangeAsync(identity, owner.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await WithDbAsync(db => db.Tenants.ExecuteUpdateAsync(setters => setters.SetProperty(tenant => tenant.Status, TenantStatus.Active), ct));
        (await ExchangeAsync(identity, owner.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PutAsync($"/api/auth/admin/applications/{product.Id}/active", new { isActive = false }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ExchangeAsync(identity, owner.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TOK01_EveryRefusalLooksTheSameSoNothingIsRevealed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        await SignUpVerifiedAsync("Outsider Co", "outsider@other.com", ct);
        var outsider = await IdentityTokenAsync(ct, "outsider@other.com");

        var realTenantNotAMember = await ExchangeAsync(outsider, owner.TenantId, product.Id, ct);
        var unknownTenant = await ExchangeAsync(outsider, Guid.NewGuid(), product.Id, ct);
        var unknownApplication = await ExchangeAsync(await IdentityTokenAsync(ct), owner.TenantId, Guid.NewGuid(), ct);

        var bodies = new List<string>();
        foreach (var response in new[] { realTenantNotAMember, unknownTenant, unknownApplication })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var body = (await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(ct))!;
            body.Remove("traceId");
            bodies.Add(body.ToJsonString());
        }

        bodies.Distinct().Count().ShouldBe(1, "a caller cannot tell a real tenant or product from an invented one");
    }

    [Fact]
    public async Task TOK01_AMissingTenantOrApplicationIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        await ProductAndOwnerAsync(ct);
        var identity = await IdentityTokenAsync(ct);

        (await PostAsync(TokenExchangeEndpoints.Route, new { }, identity, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(TokenExchangeEndpoints.Route, new { tenantId = Guid.NewGuid() }, identity, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(TokenExchangeEndpoints.Route, new { applicationId = Guid.NewGuid() }, identity, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(TokenExchangeEndpoints.Route, new { tenantId = Guid.Empty, applicationId = Guid.Empty }, identity, ct)).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TOK01_OnlyAnIdentityTokenCanBeExchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        var productToken = (await ExchangedTokenAsync(await IdentityTokenAsync(ct), owner.TenantId, product.Id, ct)).EncodedToken;

        // Not a product token (no chaining), not a product's service token, and not nothing.
        (await ExchangeAsync(productToken, owner.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ExchangeAsync(product.ServiceToken, owner.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ExchangeAsync(null, owner.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TOK01_TheTokenValidatesAgainstTheServicesKeyForItsOwnAudienceOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await ProductAndOwnerAsync(ct);
        var token = await ExchangedTokenAsync(await IdentityTokenAsync(ct), owner.TenantId, product.Id, ct);

        (await ValidateAsLocalProductAsync(token.EncodedToken, "whatsapp-saas-api")).IsValid.ShouldBeTrue();
        (await ValidateAsLocalProductAsync(token.EncodedToken, "another-product-api")).IsValid.ShouldBeFalse("another product rejects it");
    }

    private async Task<(TestProduct Product, SignupResponse Owner)> ProductAndOwnerAsync(
        CancellationToken ct, bool subscribe = true, string[]? extraPermissions = null)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        if (extraPermissions is not null)
        {
            await AddPermissionsAsync(adminToken, product.Id, extraPermissions, ct);
        }

        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        if (subscribe)
        {
            await SubscribeAsync(product, owner.TenantId, ct);
        }

        return (product, owner);
    }

    private async Task<string> IdentityTokenAsync(CancellationToken ct, string email = OwnerEmail) =>
        (await LoginForTokensAsync(email, ct)).AccessToken;

    private Task<HttpResponseMessage> ExchangeAsync(string? identityToken, Guid tenantId, Guid applicationId, CancellationToken ct) =>
        PostAsync(TokenExchangeEndpoints.Route, new { tenantId, applicationId }, identityToken, ct);

    private async Task<JsonWebToken> ExchangedTokenAsync(string identityToken, Guid tenantId, Guid applicationId, CancellationToken ct)
    {
        var response = await ExchangeAsync(identityToken, tenantId, applicationId, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<TokenExchangeResponse>(Json, ct))!;
        return new JsonWebTokenHandler().ReadJsonWebToken(body.AccessToken);
    }

    private static string[] PermissionsOf(JsonWebToken token) =>
        token.TryGetPayloadValue<string[]>("permissions", out var permissions) ? permissions : [];

    private async Task SubscribeAsync(TestProduct product, Guid tenantId, CancellationToken ct) =>
        (await PostAsync(SubscriptionEndpoints.RouteFor(tenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

    private async Task SetSubscriptionStatusAsync(TestProduct product, Guid tenantId, string status, CancellationToken ct)
    {
        var response = await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(tenantId, product.Id), $$"""{"status": "{{status}}"}""", product.ServiceToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task AddPermissionsAsync(string adminToken, Guid applicationId, string[] keys, CancellationToken ct)
    {
        var permissions = keys.Select(key => new { key, label = key }).ToArray();
        (await PostAsync($"/api/auth/admin/applications/{applicationId}/permissions", new { permissions }, adminToken, ct)).IsSuccessStatusCode.ShouldBeTrue();
    }

    /// <summary>What a product's own API does with a token: check signature, expiry and that the audience is its own.</summary>
    private async Task<TokenValidationResult> ValidateAsLocalProductAsync(string token, string ownAudience) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = ServiceSigningKey(),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidateIssuer = false,
            ValidateAudience = true,
            ValidAudience = ownAudience,
            ValidateLifetime = true,
        });
}
