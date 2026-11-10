using System.Net;
using System.Text;
using AuthService.Authorization;
using AuthService.Entitlements.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthService.Entitlements.Tests;

/// <summary>ENT-07 (AUTH-284) <c>MapAuthServiceWebhook</c>: verify, read, drop duplicates, keep only newer versions, answer quickly. Proposal item 16.</summary>
public sealed class WebhookEndpointTests
{
    private const string Secret = "whsec_test";
    private const string Path = "/auth-webhooks";
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ENT07_ASignedEntitlementsChangedIsKeptAndTheNewLimitAnswered()
    {
        await using var product = await ProductAsync();
        product.Auth.SetTenant(Acme, new Dictionary<string, long?> { ["contacts.reachable"] = 75_000 }, planKey: "growth", planName: "Growth", overridden: ["contacts.reachable"]);

        var response = await product.Http.SendAsync(SignedWebhook.Create(Path, product.Auth.EntitlementsChangedEvent(Acme), Secret), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var entitlements = product.Services.GetRequiredService<IEntitlements>();
        (await entitlements.GetLimitAsync(Acme, "contacts.reachable", Token)).ShouldBe(new LimitValue(75_000));
        product.Auth.Requests.ShouldNotContain(request => request.Contains("/tenants/", StringComparison.Ordinal), "answered from the store");
    }

    [Fact]
    public async Task ENT07_AWrongSignatureOrAnOldTimeIsRefusedAndNothingIsKept()
    {
        await using var product = await ProductAsync();
        product.Auth.SetTenant(Acme);
        var body = product.Auth.EntitlementsChangedEvent(Acme);

        var wrong = await product.Http.SendAsync(SignedWebhook.Create(Path, body, "whsec_other"), Token);
        var old = await product.Http.SendAsync(SignedWebhook.Create(Path, body, Secret, DateTimeOffset.UtcNow.AddMinutes(-10)), Token);
        var unsigned = await product.Http.PostAsync(Path, new StringContent(body, Encoding.UTF8, "application/json"), Token);

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await product.Services.GetRequiredService<IEntitlementStore>().GetAsync(Acme, Token)).ShouldBeNull();
        product.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task ENT07_TheSameEventTwiceIsHandledOnceAndAnOlderVersionNeverReplacesANewerOne()
    {
        await using var product = await ProductAsync();
        product.Auth.SetTenant(Acme, new Dictionary<string, long?> { ["contacts.reachable"] = 50_000 });
        var older = product.Auth.EntitlementsChangedEvent(Acme);
        product.Auth.SetTenant(Acme, new Dictionary<string, long?> { ["contacts.reachable"] = 60_000 });
        var newer = product.Auth.EntitlementsChangedEvent(Acme);

        (await product.Http.SendAsync(SignedWebhook.Create(Path, newer, Secret), Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await product.Http.SendAsync(SignedWebhook.Create(Path, newer, Secret), Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await product.Http.SendAsync(SignedWebhook.Create(Path, older, Secret), Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        product.Events.Count.ShouldBe(2, "the duplicate is dropped by its event id; the older event is new, but changes nothing");
        var kept = (await product.Services.GetRequiredService<IEntitlementStore>().GetAsync(Acme, Token))!;
        kept.Version.ShouldBe(2);
        kept.Get("contacts.reachable").ShouldBe(new LimitValue(60_000));
    }

    [Fact]
    public async Task ENT07_SubscribingAndSuspendingSetTheStatus()
    {
        await using var product = await ProductAsync();
        product.Auth.SetTenant(Acme, new Dictionary<string, long?> { ["contacts.reachable"] = 50_000 });

        await product.Http.SendAsync(SignedWebhook.Create(Path, product.Auth.ApplicationSubscribedEvent(Acme, maxSeats: 5), Secret), Token);
        var store = product.Services.GetRequiredService<IEntitlementStore>();
        (await store.GetAsync(Acme, Token))!.Status.ShouldBe("Active");

        await product.Http.SendAsync(SignedWebhook.Create(Path, product.Auth.ApplicationUnsubscribedEvent(Acme, "Suspended"), Secret), Token);
        var kept = (await store.GetAsync(Acme, Token))!;
        kept.Status.ShouldBe("Suspended");
        kept.Version.ShouldBe(1, "suspending does not change the limits");
        product.Events.Select(received => received.GetType()).ShouldBe([typeof(ApplicationSubscribedEvent), typeof(ApplicationUnsubscribedEvent)]);
    }

    [Fact]
    public async Task ENT07_ANewerSchemaIsRefusedSoTheAuthServiceKeepsItAndATooLargeBodyIsRefused()
    {
        await using var product = await ProductAsync();
        var newer = $$"""{"eventId":"{{Guid.NewGuid()}}","eventType":"EntitlementsChanged","occurredAt":"2026-11-10T18:30:00Z","schemaVersion":2,"tenantId":"{{Acme}}","applicationId":"{{Guid.NewGuid()}}"}""";

        var refused = await product.Http.SendAsync(SignedWebhook.Create(Path, newer, Secret), Token);
        var huge = await product.Http.SendAsync(SignedWebhook.Create(Path, $$"""{"eventId":"{{Guid.NewGuid()}}","pad":"{{new string('x', 300_000)}}"}""", Secret), Token);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Token)).ShouldContain("schema version 2");
        huge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task ENT07_WhenTheProductsOwnHandlerFailsTheEventIsHandledAgainOnTheNextDelivery()
    {
        var fail = true;
        await using var product = await ProductAsync(onEvent: _ => fail ? throw new InvalidOperationException("the product's database is down") : Task.CompletedTask);
        product.Auth.SetTenant(Acme);
        var body = product.Auth.EntitlementsChangedEvent(Acme);

        var first = await product.Http.SendAsync(SignedWebhook.Create(Path, body, Secret), Token);
        fail = false;
        var second = await product.Http.SendAsync(SignedWebhook.Create(Path, body, Secret), Token);

        first.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        product.Events.Count.ShouldBe(2, "the failed attempt was not remembered as handled");
    }

    [Fact]
    public void ENT07_AProductWithoutAWebhookSecretFailsAtStartWithAPlainMessage()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthServiceEntitlements(_ => { });
        using var app = builder.Build();

        Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => app.StartAsync(Token).GetAwaiter().GetResult())
            .Message.ShouldContain("webhook signing secret");
    }

    private static async Task<ProductHost> ProductAsync(Func<AuthWebhookEvent, Task>? onEvent = null)
    {
        var auth = new FakeAuthService().AddLimit("contacts.reachable", "Reachable contacts", 1_000, "contacts");
        var events = new List<AuthWebhookEvent>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAuthServiceClient>(auth);
        builder.Services.AddAuthServiceEntitlements(options =>
        {
            options.WebhookSecrets.Add(Secret);
            options.OnEvent = async (received, _, _) =>
            {
                lock (events)
                {
                    events.Add(received);
                }

                if (onEvent is not null)
                {
                    await onEvent(received);
                }
            };
        });

        var app = builder.Build();
        app.MapAuthServiceWebhook(Path);
        await app.StartAsync(Token);
        return new ProductHost(app, auth, events);
    }

    private sealed class ProductHost(WebApplication app, FakeAuthService auth, List<AuthWebhookEvent> events) : IAsyncDisposable
    {
        public HttpClient Http { get; } = app.GetTestClient();

        public FakeAuthService Auth => auth;

        public IServiceProvider Services => app.Services;

        public IReadOnlyList<AuthWebhookEvent> Events
        {
            get
            {
                lock (events)
                {
                    return [.. events];
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await app.DisposeAsync();
        }
    }
}
