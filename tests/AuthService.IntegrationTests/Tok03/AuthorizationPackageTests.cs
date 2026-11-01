using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.TokenExchange;
using AuthService.Api.Tokens;
using AuthService.Authorization;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.IntegrationTests.Tok03;

/// <summary>
/// TOK-03 (AUTH-28) the shared authorization package, exercised the way a product uses it: a small product API, started in the test
/// process, that installs <c>AddAuthServiceAuthorization</c> and trusts the running Auth Service only through its published keys.
/// The Jira test case ids for this story were not reachable when it was built, so the tests are named for the acceptance criteria.
/// </summary>
public sealed class AuthorizationPackageTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Audience = "whatsapp-saas-api";
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid UserId = Guid.Parse("33333333-3333-7333-8333-333333333333");

    [Fact]
    public async Task TOK03_ARealExchangedProductTokenIsAcceptedAndCarriesItsTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var catalog = new[] { "campaign.read", "campaign.create" }.Select(key => new { key, label = key }).ToArray();
        (await PostAsync($"/api/auth/admin/applications/{product.Id}/permissions", new { permissions = catalog }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var identity = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;
        var exchange = await PostAsync(TokenExchangeEndpoints.Route, new { tenantId = owner.TenantId, applicationId = product.Id }, identity, ct);
        exchange.StatusCode.ShouldBe(HttpStatusCode.OK);
        var productToken = (await exchange.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;
        await using var api = await StartProductAsync();

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", productToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid().ShouldBe(owner.TenantId);
        (await SendAsync(api, HttpMethod.Post, "/campaigns", productToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TOK03_ATokenForAnotherProductIsRefusedEvenWithAValidSignature()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", Mint(audience: "another-product-api", permissions: ["campaign.read:Full"]), ct);

        await ShouldBeUnauthorizedAsync(response, ct);
    }

    [Fact]
    public async Task TOK03_ATokenWithNoAudienceIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", Mint(audience: null, permissions: ["campaign.read:Full"]), ct);

        await ShouldBeUnauthorizedAsync(response, ct);
    }

    [Fact]
    public async Task TOK03_AnExpiredTokenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", Mint(permissions: ["campaign.read:Full"], expiresIn: TimeSpan.FromHours(-1)), ct);

        await ShouldBeUnauthorizedAsync(response, ct);
    }

    [Fact]
    public async Task TOK03_ATokenFromAnotherIssuerIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", Mint(permissions: ["campaign.read:Full"], issuer: "SomeoneElse"), ct);

        await ShouldBeUnauthorizedAsync(response, ct);
    }

    [Fact]
    public async Task TOK03_ATokenSignedWithAnotherKeyIsRefusedEvenWhenItClaimsTheServicesKeyId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = Mint(permissions: ["campaign.read:Full"], key: new ECDsaSecurityKey(attacker) { KeyId = ServiceSigningKey().KeyId });

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", forged, ct);

        await ShouldBeUnauthorizedAsync(response, ct);
    }

    [Fact]
    public async Task TOK03_ATamperedPayloadIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        var parts = Mint(tenantId: TenantA, permissions: ["campaign.read:Full"]).Split('.');
        var payload = JsonNode.Parse(Base64UrlEncoder.DecodeBytes(parts[1]))!.AsObject();
        payload["tid"] = TenantB.ToString();
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()))}.{parts[2]}";

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", tampered, ct);

        await ShouldBeUnauthorizedAsync(response, ct);
    }

    [Fact]
    public async Task TOK03_AnUnsignedTokenWithAlgNoneIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new
        {
            iss = "AuthService",
            aud = Audience,
            sub = UserId,
            tid = TenantA,
            permissions = new[] { "campaign.read:Full" },
            exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
        }));

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", $"{header}.{payload}.", ct);

        await ShouldBeUnauthorizedAsync(response, ct);
    }

    [Fact]
    public async Task TOK03_ATokenSignedWithAHmacSecretIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        var secret = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-shared-secret-an-attacker-could-pick-1234567890")) { KeyId = ServiceSigningKey().KeyId };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "AuthService",
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = UserId.ToString(), ["tid"] = TenantA.ToString(), ["permissions"] = new[] { "campaign.read:Full" } },
            SigningCredentials = new SigningCredentials(secret, SecurityAlgorithms.HmacSha256),
        });

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", token, ct);

        await ShouldBeUnauthorizedAsync(response, ct);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public async Task TOK03_AMalformedOrMissingTokenIsRefusedCleanly(string token)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", token.Length == 0 ? null : token, ct);

        await ShouldBeUnauthorizedAsync(response, ct);
    }

    [Fact]
    public async Task TOK03_AnIdentityTokenAndAServiceTokenAreNotProductTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var identity = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;
        var tenantAdmin = await TenantTokenAsync(identity, owner.TenantId, ct);
        await using var api = await StartProductAsync();

        foreach (var token in new[] { identity, product.ServiceToken, tenantAdmin })
        {
            await ShouldBeUnauthorizedAsync(await SendAsync(api, HttpMethod.Get, "/campaigns", token, ct), ct);
            await ShouldBeUnauthorizedAsync(await SendAsync(api, HttpMethod.Get, "/me", token, ct), ct);
        }
    }

    [Fact]
    public async Task TOK03_AWrongAuthorizationSchemeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/campaigns");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Mint(permissions: ["campaign.read:Full"]));

        await ShouldBeUnauthorizedAsync(await api.Client.SendAsync(request, ct), ct);
    }

    [Theory]
    [InlineData("campaign.read:ReadOnly", "GET", HttpStatusCode.OK)]
    [InlineData("campaign.read:ReadOnly", "POST", HttpStatusCode.Forbidden)]
    [InlineData("campaign.read:ReadOnly", "DELETE", HttpStatusCode.Forbidden)]
    [InlineData("campaign.read:CreateRead", "GET", HttpStatusCode.OK)]
    [InlineData("campaign.read:Full", "GET", HttpStatusCode.OK)]
    [InlineData("campaign.create:ReadOnly", "POST", HttpStatusCode.Forbidden)]
    [InlineData("campaign.create:CreateRead", "POST", HttpStatusCode.OK)]
    [InlineData("campaign.create:Full", "POST", HttpStatusCode.OK)]
    [InlineData("campaign.delete:CreateRead", "DELETE", HttpStatusCode.Forbidden)]
    [InlineData("campaign.delete:Full", "DELETE", HttpStatusCode.OK)]
    public async Task TOK03_AnEndpointNeedsItsPermissionAtItsLevelOrHigher(string permission, string method, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        var path = method == "DELETE" ? "/campaigns/42" : "/campaigns";

        var response = await SendAsync(api, new HttpMethod(method), path, Mint(permissions: [permission]), ct);

        response.StatusCode.ShouldBe(expected);
        if (expected == HttpStatusCode.Forbidden)
        {
            (await ReadProblemAsync(response, ct)).Code.ShouldBe("forbidden");
        }
    }

    [Fact]
    public async Task TOK03_APermissionForAnotherEndpointOrNoPermissionAtAllIsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        foreach (var permissions in new[] { Array.Empty<string>(), ["campaign.create:Full"], ["campaign.read:None"], ["campaign.read"], ["campaign.read:full"], ["campaign.read:2"] })
        {
            var response = await SendAsync(api, HttpMethod.Get, "/campaigns", Mint(permissions: permissions), ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"permissions [{string.Join(", ", permissions)}]");
        }
    }

    [Fact]
    public async Task TOK03_TheHighestLevelOfARepeatedPermissionWins()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        var response = await SendAsync(api, HttpMethod.Post, "/campaigns", Mint(permissions: ["campaign.create:ReadOnly", "campaign.create:Full"]), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TOK03_ARoleNameInTheTokenGrantsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        var response = await SendAsync(api, HttpMethod.Get, "/campaigns", Mint(permissions: [], extraClaims: new() { ["role"] = "Owner", ["roles"] = new[] { "Owner", "Admin" } }), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TOK03_TheTenantIsTheTokensNeverTheRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/campaigns?tenantId={TenantB}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Mint(tenantId: TenantA, permissions: ["campaign.read:Full"]));
        request.Headers.Add("X-Tenant-Id", TenantB.ToString());

        var response = await api.Client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("tenantId").GetGuid().ShouldBe(TenantA, "the handler reads the tenant from the token's tid");
        body.GetProperty("decoyQuery").GetString().ShouldBe(TenantB.ToString(), "the decoys were sent, and ignored");
    }

    [Fact]
    public async Task TOK03_ATokenWithoutATenantCannotUseATenantEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        (await SendAsync(api, HttpMethod.Get, "/me", Mint(withoutTenant: true, permissions: ["campaign.read:Full"]), ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(api, HttpMethod.Get, "/campaigns", Mint(withoutTenant: true, permissions: ["campaign.read:Full"]), ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(api, HttpMethod.Get, "/me", Mint(tenantId: TenantA, permissions: []), ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "a tenant is enough for RequireTenant()");
    }

    [Fact]
    public async Task TOK03_AMalformedTenantClaimIsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        var response = await SendAsync(api, HttpMethod.Get, "/me", Mint(permissions: [], extraClaims: new() { ["tid"] = "not-a-guid" }), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TOK03_AnEndpointWithNoPolicyStillNeedsNothing_ButTheHealthCheckIsOpenOnPurpose()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();

        (await api.Client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await api.Client.GetAsync("/campaigns", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TOK03_RefusalsAreProblemJsonWithNoStackTraceOrExceptionText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        var bad = new[] { "not-a-token", Mint(audience: "another-product-api", permissions: []), Mint(permissions: [], expiresIn: TimeSpan.FromHours(-1)) };

        foreach (var token in bad)
        {
            var response = await SendAsync(api, HttpMethod.Get, "/campaigns", token, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
            response.Headers.WwwAuthenticate.Select(challenge => challenge.Scheme).ShouldContain("Bearer");
            var body = await response.Content.ReadAsStringAsync(ct);
            body.ShouldNotContain("Exception", Case.Insensitive);
            body.ShouldNotContain(" at ", Case.Sensitive);
            body.ShouldNotContain("IDX", Case.Sensitive);
            body.ShouldNotContain("audience", Case.Insensitive);
            body.ShouldNotContain("expired", Case.Insensitive);
            body.ShouldNotContain("signature", Case.Insensitive);
        }
    }

    [Fact]
    public async Task TOK03_ManyRequestsFetchTheKeysOnlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        var token = Mint(permissions: ["campaign.read:Full"]);

        for (var i = 0; i < 25; i++)
        {
            (await SendAsync(api, HttpMethod.Get, "/campaigns", token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        api.KeyFetchCount.ShouldBe(1, "a token is checked locally; the product does not call the Auth Service per request");
    }

    [Fact]
    public async Task TOK03_ConcurrentFirstRequestsShareOneKeyFetch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        var token = Mint(permissions: ["campaign.read:Full"]);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SendAsync(api, HttpMethod.Get, "/campaigns", token, ct)));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        api.KeyFetchCount.ShouldBe(1);
    }

    [Fact]
    public async Task TOK03_ANewKeyAfterARotationIsPickedUpWithoutARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        await using var api = await StartProductAsync();
        var oldToken = Mint(permissions: ["campaign.read:Full"]);
        (await SendAsync(api, HttpMethod.Get, "/campaigns", oldToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var scope = Harness.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SigningKeyService>().RotateNowAsync(AuditActor.System, ct);
        }

        var newToken = Mint(permissions: ["campaign.read:Full"]);
        await Eventually.UntilAsync(
            async () => (await SendAsync(api, HttpMethod.Get, "/campaigns", newToken, ct)).StatusCode == HttpStatusCode.OK,
            "a token from the new key is accepted once the product has refreshed its keys");
        api.KeyFetchCount.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task TOK03_ARotatedOutKeyStopsValidatingOnceTheProductHasRefreshed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        var oldToken = Mint(permissions: ["campaign.read:Full"]);
        (await SendAsync(api, HttpMethod.Get, "/campaigns", oldToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var scope = Harness.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SigningKeyService>().RotateNowAsync(AuditActor.System, ct);
        }

        // The product learns of the new key when it meets a token signed by it, which also drops the revoked one.
        var newToken = Mint(permissions: ["campaign.read:Full"]);
        await Eventually.UntilAsync(
            async () => (await SendAsync(api, HttpMethod.Get, "/campaigns", newToken, ct)).StatusCode == HttpStatusCode.OK,
            "the product refreshes its keys");

        (await SendAsync(api, HttpMethod.Get, "/campaigns", oldToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TOK03_ProductsKeepWorkingWhenTheAuthServiceIsUnreachableAfterTheFirstFetch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        var token = Mint(permissions: ["campaign.read:Full"]);
        (await SendAsync(api, HttpMethod.Get, "/campaigns", token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        api.KeyFetchesFail = true;

        for (var i = 0; i < 5; i++)
        {
            (await SendAsync(api, HttpMethod.Get, "/campaigns", token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the last good key set is kept");
        }
    }

    [Fact]
    public async Task TOK03_WhenTheKeysCannotBeFetchedAtAllEveryTokenIsRefusedNotAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await StartProductAsync();
        api.KeyFetchesFail = true;

        var token = Mint(permissions: ["campaign.read:Full"]);

        await ShouldBeUnauthorizedAsync(await SendAsync(api, HttpMethod.Get, "/campaigns", token, ct), ct);
        for (var i = 0; i < 10; i++)
        {
            (await SendAsync(api, HttpMethod.Get, "/campaigns", token, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        api.KeyFetchCount.ShouldBeLessThan(5, "an Auth Service that is down is not hammered by every request");

        api.KeyFetchesFail = false;
        await Eventually.UntilAsync(
            async () => (await SendAsync(api, HttpMethod.Get, "/campaigns", token, ct)).StatusCode == HttpStatusCode.OK,
            "the product recovers by itself once the Auth Service is back");
    }

    [Fact]
    public async Task TOK03_ASecondProductWithItsOwnAudienceRejectsTheFirstProductsTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var first = await StartProductAsync();
        await using var second = await StartProductAsync(audience: "second-product-api");
        var forFirst = Mint(audience: Audience, permissions: ["campaign.read:Full"]);

        (await SendAsync(first, HttpMethod.Get, "/campaigns", forFirst, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldBeUnauthorizedAsync(await SendAsync(second, HttpMethod.Get, "/campaigns", forFirst, ct), ct);
    }

    [Theory]
    [InlineData(null, "https://auth.example.com/.well-known/jwks.json", "Audience")]
    [InlineData("", "https://auth.example.com/.well-known/jwks.json", "Audience")]
    [InlineData("my-api", null, "JwksUrl")]
    [InlineData("my-api", "http://auth.example.com/.well-known/jwks.json", "https")]
    public async Task TOK03_AProductWithMissingOrUnsafeSettingsFailsAtStartupNotAtTheFirstRequest(string? audience, string? jwksUrl, string mentions)
    {
        var failure = await Should.ThrowAsync<OptionsValidationException>(async () =>
        {
            await using var api = await SampleProductApi.StartAsync(Client, audience ?? string.Empty, options =>
            {
                options.Audience = audience!;
                options.JwksUrl = jwksUrl is null ? null : new Uri(jwksUrl);
                options.AllowInsecureJwksUrl = false;
            });
        });

        failure.Message.ShouldContain(mentions);
    }

    [Fact]
    public async Task TOK03_TheAuditFindsEndpointsThatForgotAPermissionPolicy()
    {
        await using var api = await StartProductAsync(addEndpoints: app =>
        {
            app.MapGet("/forgotten", () => "oops");
            app.MapGet("/only-signed-in", () => "hi").RequireAuthorization();
            app.MapGet("/only-a-tenant-ok", () => "hi").RequireTenant();
        });

        var open = await EndpointPolicyAudit.FindEndpointsWithoutPermissionPolicyAsync(api.Services, endpoint => endpoint.RoutePattern.RawText == "/health");

        open.ShouldBe(["GET /forgotten", "GET /me", "GET /only-a-tenant-ok", "GET /only-signed-in"]);
    }

    [Fact]
    public async Task TOK03_TheAuditPassesAHostWhoseEveryEndpointIsProtectedOrExempt()
    {
        await using var api = await StartProductAsync();

        var open = await EndpointPolicyAudit.FindEndpointsWithoutPermissionPolicyAsync(
            api.Services, endpoint => endpoint.RoutePattern.RawText == "/health", requirePermission: false);

        open.ShouldBeEmpty();
    }

    [Fact]
    public async Task TOK03_TheAuditDoesNotExemptWhatItWasNotToldTo()
    {
        await using var api = await StartProductAsync();

        var open = await EndpointPolicyAudit.FindEndpointsWithoutPermissionPolicyAsync(api.Services);

        open.ShouldContain("GET /health", "an anonymous endpoint is reported unless it is named as exempt");
    }

    private Task<SampleProductApi> StartProductAsync(string audience = Audience, Action<WebApplication>? addEndpoints = null) =>
        SampleProductApi.StartAsync(Client, audience, addEndpoints: addEndpoints);

    private static Task<HttpResponseMessage> SendAsync(SampleProductApi api, HttpMethod method, string path, string? bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return api.Client.SendAsync(request, ct);
    }

    private async Task ShouldBeUnauthorizedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("unauthorized");
    }

    /// <summary>A product token as the Auth Service would sign it, or a deliberately wrong one. The tenant is A unless said otherwise.</summary>
    private string Mint(
        string? audience = Audience,
        Guid? tenantId = null,
        bool withoutTenant = false,
        string[]? permissions = null,
        TimeSpan? expiresIn = null,
        string issuer = "AuthService",
        SecurityKey? key = null,
        Dictionary<string, object>? extraClaims = null)
    {
        var now = DateTime.UtcNow;
        var lifetime = expiresIn ?? TimeSpan.FromMinutes(15);
        var claims = new Dictionary<string, object> { ["sub"] = UserId.ToString(), ["permissions"] = permissions ?? [] };
        if (!withoutTenant)
        {
            claims["tid"] = (tenantId ?? TenantA).ToString();
        }

        foreach (var (name, value) in extraClaims ?? [])
        {
            claims[name] = value;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = lifetime < TimeSpan.Zero ? now + lifetime - TimeSpan.FromHours(1) : now,
            Expires = now + lifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? ServiceSigningKey(), SecurityAlgorithms.EcdsaSha256),
        });
    }
}
