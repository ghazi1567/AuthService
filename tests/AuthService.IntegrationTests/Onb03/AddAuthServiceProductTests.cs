using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.TokenExchange;
using AuthService.Authorization;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthService.IntegrationTests.Onb03;

/// <summary>
/// ONB-03 (AUTH-274) A product set up with <c>AddAuthServiceProduct</c>: only the Auth Service's address and the client secret are given,
/// and the package learns the issuer, the product's own audience and where the keys are from the running Auth Service, then enforces
/// tokens exactly as the other way of installing it does. A small product API is hosted in the test process and talks to the real API.
/// The tests have no workbook Test Case ID: the story's acceptance criteria cover them.
/// </summary>
public sealed class AddAuthServiceProductTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task ONB03_AProductGivenOnlyTheAddressAndTheSecretAcceptsItsRealTokenAndSubscribesAnOrganisation()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var product = await StartProductAsync(world.Credential);

        var subscription = await product.Client.SubscribeTenantAsync(world.TenantId, 5, ct);
        var token = await ProductTokenAsync(world.Product.Id, world.TenantId, ct);
        var response = await product.Http.SendAsync(Get("/campaigns", token), ct);

        subscription.Status.ShouldBe("Active");
        subscription.MaxSeats.ShouldBe(5);
        subscription.ApplicationId.ShouldBe(world.Product.Id);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid().ShouldBe(world.TenantId);
        (await product.Client.SubscribeTenantAsync(world.TenantId, 99, ct)).MaxSeats.ShouldBe(5, "subscribing again changes nothing");
    }

    [Fact]
    public async Task ONB03_ATokenForAnotherProductIsRefusedEvenThoughTheSignatureIsGood()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        var other = await CreateActiveProductAsync(world.AdminToken, "other", ct);
        await PostAsync($"/api/auth/tenants/{world.TenantId}/subscriptions", new { }, other.ServiceToken, ct);
        await using var product = await StartProductAsync(world.Credential);
        await product.Client.SubscribeTenantAsync(world.TenantId, null, ct);

        var forOther = await ProductTokenAsync(other.Id, world.TenantId, ct);
        var response = await product.Http.SendAsync(Get("/campaigns", forOther), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemCodeAsync(response, ct)).ShouldBe("unauthorized");
    }

    [Fact]
    public async Task ONB03_AnIdentityTokenAndAServiceTokenAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var product = await StartProductAsync(world.Credential);
        var identity = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        var service = await product.Client.GetServiceTokenAsync(ct);

        (await product.Http.SendAsync(Get("/campaigns", identity), ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await product.Http.SendAsync(Get("/campaigns", service), ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await product.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/campaigns"), ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ONB03_WhatWasLearnedIsKeptSoTheAuthServiceIsNotCalledPerRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var product = await StartProductAsync(world.Credential);
        await product.Client.SubscribeTenantAsync(world.TenantId, null, ct);
        var token = await ProductTokenAsync(world.Product.Id, world.TenantId, ct);

        for (var i = 0; i < 5; i++)
        {
            (await product.Http.SendAsync(Get("/campaigns", token), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        product.Calls("/.well-known/auth-service-configuration").ShouldBe(1);
        product.Calls("/api/auth/applications/me").ShouldBe(1);
        product.Calls("/api/auth/token").ShouldBe(1, "the service token is kept, and the subscribe call used the same one");
        product.Calls("/.well-known/jwks.json").ShouldBe(1);
    }

    [Fact]
    public async Task ONB03_IfTheAuthServiceCannotBeReachedAtFirstEveryTokenIsRefusedAndTheProductRecoversByItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var product = await StartProductAsync(world.Credential);
        await product.Client.SubscribeTenantAsync(world.TenantId, null, ct);
        var token = await ProductTokenAsync(world.Product.Id, world.TenantId, ct);
        product.Fail = true;
        // A product that has never learned anything: start another one while the Auth Service is unreachable.
        await using var cold = await StartProductAsync(world.Credential, fail: true);

        var refused = await cold.Http.SendAsync(Get("/campaigns", token), ct);
        cold.Fail = false;
        await Task.Delay(TimeSpan.FromMilliseconds(1300), ct);
        var recovered = await cold.Http.SendAsync(Get("/campaigns", token), ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a plain 401, not an error, while nothing is known");
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ONB03_AWrongSecretRefusesEveryTokenWithoutAnErrorAndTheClientSaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var good = await StartProductAsync(world.Credential);
        await good.Client.SubscribeTenantAsync(world.TenantId, null, ct);
        var token = await ProductTokenAsync(world.Product.Id, world.TenantId, ct);
        await using var wrong = await StartProductAsync($"{world.Product.ClientId}.not-the-secret");

        var response = await wrong.Http.SendAsync(Get("/campaigns", token), ct);
        var thrown = await Should.ThrowAsync<AuthServiceException>(() => wrong.Client.GetProductAsync(ct));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        thrown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        thrown.Code.ShouldBe("invalid_client");
        thrown.Message.ShouldNotContain("not-the-secret");
    }

    [Fact]
    public async Task ONB03_TheClientReadsTheProductsOwnRecordAndExplainsARefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var product = await StartProductAsync(world.Credential);

        var record = await product.Client.GetProductAsync(ct);
        var refused = await Should.ThrowAsync<AuthServiceException>(() => product.Client.SubscribeTenantAsync(Guid.NewGuid(), null, ct));

        record.Key.ShouldBe("marketing");
        record.Audience.ShouldBe("marketing-api");
        record.IsActive.ShouldBeTrue();
        record.PermissionKeys.ShouldBe(["campaign.create", "campaign.read"]);
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        refused.Code.ShouldBe("tenant_not_found");
    }

    [Fact]
    public async Task ONB03_TheOlderClientIdAndBareSecretPairWorksToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var product = await StartProductAsync(
            world.Product.ClientSecret, configure: options => options.ClientId = world.Product.ClientId);
        await product.Client.SubscribeTenantAsync(world.TenantId, null, ct);
        var token = await ProductTokenAsync(world.Product.Id, world.TenantId, ct);

        (await product.Http.SendAsync(Get("/campaigns", token), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ONB03_ASetupThatCannotWorkFailsAtStartWithAPlainMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        var noUrl = await Should.ThrowAsync<OptionsValidationException>(() => StartProductAsync("cl_a.b", configure: options => options.Url = null));
        var plainHttp = await Should.ThrowAsync<OptionsValidationException>(
            () => StartProductAsync("cl_a.b", configure: options => options.AllowInsecureUrl = false));
        var noSecret = await Should.ThrowAsync<OptionsValidationException>(() => StartProductAsync("  "));

        string.Join(" ", noUrl.Failures).ShouldContain("Url is required");
        string.Join(" ", plainHttp.Failures).ShouldContain("must be https");
        string.Join(" ", noSecret.Failures).ShouldContain("ClientSecret is required");
        ct.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task ONB03_ATokenWithItsIssuerEditedIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var product = await StartProductAsync(world.Credential);
        await product.Client.SubscribeTenantAsync(world.TenantId, null, ct);
        var token = await ProductTokenAsync(world.Product.Id, world.TenantId, ct);
        (await product.Http.SendAsync(Get("/campaigns", token), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The same token, with its payload edited to name another issuer: the signature no longer holds, and the issuer is not the Auth Service's either.
        var parts = token.Split('.');
        var payload = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '='));
        var edited = System.Text.Encoding.UTF8.GetString(payload).Replace("\"iss\":\"AuthService\"", "\"iss\":\"Somebody\"");
        var forged = $"{parts[0]}.{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(edited)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{parts[2]}";

        (await product.Http.SendAsync(Get("/campaigns", forged), ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private sealed record World(string AdminToken, TestProduct Product, string Credential, Guid TenantId);

    /// <summary>A registered, available product with a catalogue, and an organisation with a verified owner, not yet subscribed.</summary>
    private async Task<World> SetUpAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var registered = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new
            {
                key = "marketing",
                name = "Marketing",
                jwtAudience = "marketing-api",
                permissions = new object[] { new { key = "campaign.read", label = "View" }, new { key = "campaign.create", label = "Create" } },
                activate = true,
            },
            adminToken,
            ct);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = (await registered.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        return new World(
            adminToken,
            new TestProduct(body.Id, body.Key, body.ClientId, body.ClientSecret, ""),
            body.Credential,
            owner.TenantId);
    }

    private async Task<string> ProductTokenAsync(Guid applicationId, Guid tenantId, CancellationToken ct)
    {
        var identity = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        var exchange = await PostAsync(TokenExchangeEndpoints.Route, new { tenantId, applicationId }, identity, ct);
        exchange.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await exchange.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString();

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<ProductHost> StartProductAsync(string secret, bool fail = false, Action<AuthServiceProductOptions>? configure = null)
    {
        var forwarding = new ForwardingHandler(Client) { Fail = fail };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthServiceProduct(options =>
        {
            options.Url = new Uri("http://auth.test");
            options.ClientSecret = secret;
            options.AllowInsecureUrl = true;

            // Short, so a test that needs another attempt does not wait long. The default of 30 seconds is the real-world setting.
            options.MinimumRefreshSeconds = 1;
            configure?.Invoke(options);
        });
        builder.Services.AddHttpClient(AuthServiceProductOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => forwarding);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/campaigns", (ClaimsPrincipal user) => Results.Ok(new { tenantId = user.RequireTenantId() }))
            .RequirePermission("campaign.read", AccessLevel.ReadOnly);

        await app.StartAsync();
        return new ProductHost(app, forwarding);
    }

    /// <summary>A product API hosted in the test process, and the calls it made to the Auth Service.</summary>
    private sealed class ProductHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ForwardingHandler _forwarding;

        public ProductHost(WebApplication app, ForwardingHandler forwarding)
        {
            _app = app;
            _forwarding = forwarding;
            Http = app.GetTestClient();
            Client = app.Services.GetRequiredService<IAuthServiceClient>();
        }

        public HttpClient Http { get; }

        public IAuthServiceClient Client { get; }

        public bool Fail
        {
            get => _forwarding.Fail;
            set => _forwarding.Fail = value;
        }

        public int Calls(string path) => _forwarding.Calls(path);

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Sends the product's calls to the running Auth Service (in this process), counting them by path, and can fail them all.</summary>
    private sealed class ForwardingHandler(HttpClient authService) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _calls = new();

        public bool Fail { get; set; }

        public int Calls(string path) => _calls.GetValueOrDefault(path);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls.AddOrUpdate(request.RequestUri!.AbsolutePath, 1, (_, count) => count + 1);
            if (Fail)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri.PathAndQuery) { Content = request.Content };
            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return await authService.SendAsync(forwarded, cancellationToken);
        }
    }
}
