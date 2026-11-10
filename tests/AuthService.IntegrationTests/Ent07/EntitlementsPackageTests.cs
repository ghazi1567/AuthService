using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Webhooks;
using AuthService.Authorization;
using AuthService.Entitlements;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthService.IntegrationTests.Ent07;

/// <summary>
/// ENT-07 (AUTH-284) A product using <c>AuthService.Entitlements</c> against the running Auth Service: the reads go through the product's own
/// <see cref="IAuthServiceClient"/>, and the real webhook worker delivers a real signed <c>EntitlementsChanged</c> to the package's endpoint,
/// which keeps it, so the product answers the new limit without asking the Auth Service.
/// </summary>
public sealed class EntitlementsPackageTests : AuthApiTestBase
{
    private const string AdminEmail = "ops@platform.test";
    private const string CallbackUrl = "https://product.test/auth-webhooks";
    private readonly ProductForwarder _webhooks = new();

    public EntitlementsPackageTests(PostgresContainerFixture postgres)
        : base(postgres)
    {
    }

    protected override HarnessOptions Options => new()
    {
        Settings = { ["Auth:Webhooks:DispatcherEnabled"] = "true", ["Auth:Webhooks:DispatchIntervalMilliseconds"] = "100" },
        ConfigureServices = services => services.AddHttpClient(WebhookHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => _webhooks),
    };

    [Fact]
    public async Task ENT07_TheProductReadsLimitsPlansAndEveryTenantThroughItsOwnClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var product = await StartProductAsync(world, ct);
        var client = product.Services.GetRequiredService<IEntitlementsClient>();

        var limits = await client.GetLimitsAsync(ct);
        var plans = await client.GetPlansAsync(ct);
        var tenant = await client.GetTenantAsync(world.TenantId, ct);
        var changes = await client.GetChangesAsync(cancellationToken: ct);

        limits.Select(limit => (limit.Key, limit.Default)).ShouldBe([("contacts.reachable", new LimitValue(1_000)), ("storage.assets", LimitValue.Unlimited)]);
        plans.ShouldHaveSingleItem().Values["contacts.reachable"].ShouldBe(new LimitValue(50_000));
        tenant.ShouldNotBeNull();
        tenant.PlanKey.ShouldBe("growth");
        tenant.Status.ShouldBe("Active");
        tenant.Get("contacts.reachable").ShouldBe(new LimitValue(50_000));
        changes.Items.ShouldHaveSingleItem().ChangeNumber.ShouldBe(tenant.ChangeNumber);
        (await client.GetTenantAsync(Guid.NewGuid(), ct)).ShouldBeNull("an organisation that never subscribed");
    }

    [Fact]
    public async Task ENT07_ARealSignedEventIsVerifiedAndKeptSoTheNewLimitIsAnsweredFromTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await using var product = await StartProductAsync(world, ct);
        var entitlements = product.Services.GetRequiredService<IEntitlements>();
        (await entitlements.GetLimitAsync(world.TenantId, "contacts.reachable", ct)).ShouldBe(new LimitValue(50_000), "read once from the Auth Service");

        await PutAsync($"/api/auth/admin/tenants/{world.TenantId}/subscriptions/{world.ProductId}/overrides/contacts.reachable",
            new { value = 75_000, reason = "Goodwill" }, world.AdminToken, ct);

        var store = product.Services.GetRequiredService<IEntitlementStore>();
        try
        {
            // Subscribed on the plan (version 1), then the override (version 2).
            await Eventually.UntilAsync(async () => (await store.GetAsync(world.TenantId, ct))?.Version >= 2, "the EntitlementsChanged event is kept");
        }
        catch (ShouldAssertException)
        {
            // Say what the worker saw, so a failure here can be read without a debugger.
            var rows = new List<string>();
            await WithDbAsync(async db => rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.WebhookOutbox.Select(message => $"{message.EventType} {message.Status} attempts={message.Attempts} error={message.LastError}"), ct));
            throw new ShouldAssertException($"Not kept. Outbox: {string.Join("; ", rows)}. Product answered: {string.Join(", ", _webhooks.Answers)}. Kept version: {(await store.GetAsync(world.TenantId, ct))?.Version}");
        }

        var reads = product.AuthServiceCalls;
        (await entitlements.GetLimitAsync(world.TenantId, "contacts.reachable", ct)).ShouldBe(new LimitValue(75_000));
        (await store.GetAsync(world.TenantId, ct))!.IsOverridden("contacts.reachable").ShouldBeTrue();
        product.AuthServiceCalls.ShouldBe(reads, "answered from the store");
        _webhooks.Answers.ShouldAllBe(status => status == HttpStatusCode.NoContent);
    }

    private async Task<World> SetUpAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var registered = await PostAsync(ApplicationAdminEndpoints.Route, new
        {
            key = "marketing",
            name = "Marketing",
            jwtAudience = "marketing-api",
            permissions = new object[] { new { key = "campaign.read", label = "View" } },
            activate = true,
        }, adminToken, ct);
        var body = (await registered.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        var product = await CreateProductTokenAsync(body, ct);

        var webhook = await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(CallbackUrl, false, null), product, ct);
        var secret = (await webhook.Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!.SigningSecret!;
        await PostAsync($"/api/auth/admin/applications/{body.Id}/limits", new
        {
            limits = new object?[]
            {
                new { key = "contacts.reachable", label = "Reachable contacts", unit = "contacts", defaultValue = 1_000 },
                new { key = "storage.assets", label = "Asset storage", unit = "MB", defaultValue = (long?)null },
            },
        }, adminToken, ct);
        await PostAsync($"/api/auth/admin/applications/{body.Id}/plans",
            new { key = "growth", name = "Growth", values = new Dictionary<string, long?> { ["contacts.reachable"] = 50_000 } }, adminToken, ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        (await PostAsync($"/api/auth/tenants/{owner.TenantId}/subscriptions", new { planKey = "growth" }, product, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        return new World(adminToken, body.Id, body.Credential, secret, owner.TenantId);
    }

    private async Task<string> CreateProductTokenAsync(RegisterApplicationResponse product, CancellationToken ct)
    {
        var token = await PostAsync("/api/auth/token",
            new { grant_type = "client_credentials", client_id = product.ClientId, client_secret = product.ClientSecret }, null, ct);
        token.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await token.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct)).GetProperty("access_token").GetString()!;
    }

    private async Task<ProductHost> StartProductAsync(World world, CancellationToken ct)
    {
        var calls = new CountingForwarder(Client);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthServiceProduct(options =>
        {
            options.Url = new Uri("http://auth.test");
            options.ClientSecret = world.Credential;
            options.AllowInsecureUrl = true;
        });
        builder.Services.AddHttpClient(AuthServiceProductOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => calls);
        builder.Services.AddAuthServiceEntitlements(options => options.WebhookSecrets.Add(world.WebhookSecret));

        var app = builder.Build();
        app.MapAuthServiceWebhook("/auth-webhooks");
        await app.StartAsync(ct);
        _webhooks.Target = app.GetTestServer().CreateHandler();
        return new ProductHost(app, calls);
    }

    private sealed record World(string AdminToken, Guid ProductId, string Credential, string WebhookSecret, Guid TenantId);

    private sealed class ProductHost(WebApplication app, CountingForwarder calls) : IAsyncDisposable
    {
        public IServiceProvider Services => app.Services;

        public int AuthServiceCalls => calls.Count;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    /// <summary>The product's calls to the Auth Service, sent to the API in this process and counted.</summary>
    private sealed class CountingForwarder(HttpClient authService) : HttpMessageHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            var forwarded = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery) { Content = request.Content };
            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return await authService.SendAsync(forwarded, cancellationToken);
        }
    }

    /// <summary>The Auth Service's webhook calls, handed to the product's in-process server once it has started.</summary>
    private sealed class ProductForwarder : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<HttpStatusCode> _answers = new();

        public HttpMessageHandler? Target { get; set; }

        public IReadOnlyCollection<HttpStatusCode> Answers => [.. _answers];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Target is not { } target)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            using var invoker = new HttpMessageInvoker(target, disposeHandler: false);
            var response = await invoker.SendAsync(request, cancellationToken);
            _answers.Enqueue(response.StatusCode);
            return response;
        }
    }
}
