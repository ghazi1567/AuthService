using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Webhooks;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.IntegrationTests.Sub07;

/// <summary>
/// SUB-07 (AUTH-206): rotating a product's webhook secret keeps the old one signing for an overlap, so a receiver that has not been
/// updated yet still verifies events. The real worker signs and posts to a fake product, on a controllable clock. The tests have no
/// workbook Test Case ID: the story's acceptance criteria cover them.
/// </summary>
public sealed class WebhookSecretOverlapTests : AuthApiTestBase
{
    private const string AdminEmail = "ops@platform.test";
    private const string CallbackUrl = "https://product.test/hooks";

    // Starts on a whole microsecond: the database stores microseconds.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));
    private readonly FakeWebhookReceiver _product;

    public WebhookSecretOverlapTests(PostgresContainerFixture postgres)
        : base(postgres)
    {
        _product = new FakeWebhookReceiver(_clock);
    }

    protected override HarnessOptions Options => new()
    {
        Settings = { ["Auth:Webhooks:DispatcherEnabled"] = "true" },
        ConfigureServices = services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddHttpClient(WebhookHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => _product);
        },
    };

    [Fact]
    public async Task SUB07_AfterARotationEventsCarryASignatureForTheNewSecretAndForTheOldOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);
        var rotation = await RotateAsync(t, overlapHours: null, ct);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "the event arrives");

        var signatures = SignaturesOf(_product.Requests[0]);
        signatures.Count.ShouldBe(2);
        var timestamp = long.Parse(_product.Requests[0].Headers["X-Auth-Timestamp"]);
        var body = _product.Requests[0].Body;
        signatures[0].ShouldBe(IndependentSignature(rotation.NewSecret, timestamp, body), "newest first");
        signatures[1].ShouldBe(IndependentSignature(t.FirstSecret, timestamp, body));

        // A receiver that still has only the old secret, and one that has already switched, both accept the event.
        ReceiverAccepts(_product.Requests[0], t.FirstSecret).ShouldBeTrue();
        ReceiverAccepts(_product.Requests[0], rotation.NewSecret).ShouldBeTrue();
        ReceiverAccepts(_product.Requests[0], "whsec_not_either_of_them").ShouldBeFalse();
    }

    [Fact]
    public async Task SUB07_TheRotationSaysWhenTheOldSecretStopsSigningAndTheConfigShowsItUntilThen()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);

        var rotation = await RotateAsync(t, overlapHours: null, ct);

        rotation.PreviousSecretExpiresAt.ShouldBe(_clock.GetUtcNow().AddHours(24), "the default overlap is 24 hours");
        (await ConfigAsync(t, ct)).PreviousSecretExpiresAt.ShouldBe(rotation.PreviousSecretExpiresAt);

        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        (await ConfigAsync(t, ct)).PreviousSecretExpiresAt.ShouldBeNull("the overlap is over");
    }

    [Fact]
    public async Task SUB07_AfterTheOverlapTheOldSecretIsDiscardedAndSignsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);
        var rotation = await RotateAsync(t, overlapHours: null, ct);
        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "an event inside the overlap");
        SignaturesOf(_product.Requests[0]).Count.ShouldBe(2);

        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        await SubscribeNewTenantAsync(t, "Other Co", "boss@other.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 2, "an event after it");

        var late = _product.Requests[1];
        var signatures = SignaturesOf(late);
        signatures.ShouldBe([IndependentSignature(rotation.NewSecret, long.Parse(late.Headers["X-Auth-Timestamp"]), late.Body)]);
        ReceiverAccepts(late, t.FirstSecret).ShouldBeFalse("the old secret no longer signs anything");
        await Eventually.UntilAsync(async () => (await StoredAsync(t, ct)).Previous is null, "it is thrown away, not only ignored");
        (await StoredAsync(t, ct)).PreviousExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task SUB07_ASecondRotationInsideAnOverlapDropsTheOldestSecretSoThereAreNeverThree()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);
        var second = await RotateAsync(t, overlapHours: null, ct);
        _clock.Advance(TimeSpan.FromHours(1));
        var third = await RotateAsync(t, overlapHours: null, ct);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "the event arrives");

        var request = _product.Requests[0];
        var timestamp = long.Parse(request.Headers["X-Auth-Timestamp"]);
        SignaturesOf(request).ShouldBe(
            [IndependentSignature(third.NewSecret, timestamp, request.Body), IndependentSignature(second.NewSecret, timestamp, request.Body)],
            "the newest and the one before it");
        ReceiverAccepts(request, t.FirstSecret).ShouldBeFalse("the first secret was the oldest, and is gone");
        third.PreviousSecretExpiresAt.ShouldBe(_clock.GetUtcNow().AddHours(24), "the overlap starts again from the second rotation");
    }

    [Fact]
    public async Task SUB07_RotatingWithNoOverlapCutsTheOldSecretOffAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);

        var rotation = await RotateAsync(t, overlapHours: 0, ct);

        rotation.PreviousSecretExpiresAt.ShouldBeNull();
        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "the event arrives");
        var request = _product.Requests[0];
        SignaturesOf(request).ShouldBe([IndependentSignature(rotation.NewSecret, long.Parse(request.Headers["X-Auth-Timestamp"]), request.Body)]);
        ReceiverAccepts(request, t.FirstSecret).ShouldBeFalse("a leaked secret signs nothing from this moment");
        (await StoredAsync(t, ct)).Previous.ShouldBeNull();
    }

    [Fact]
    public async Task SUB07_ANoOverlapRotationInsideAnOverlapAlsoDropsTheOneStillSigning()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);
        await RotateAsync(t, overlapHours: null, ct);
        var third = await RotateAsync(t, overlapHours: 0, ct);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "the event arrives");

        SignaturesOf(_product.Requests[0]).Count.ShouldBe(1);
        ReceiverAccepts(_product.Requests[0], third.NewSecret).ShouldBeTrue();
        (await StoredAsync(t, ct)).Previous.ShouldBeNull();
    }

    [Fact]
    public async Task SUB07_ARotationCanAskForItsOwnOverlap()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);

        var rotation = await RotateAsync(t, overlapHours: 2, ct);

        rotation.PreviousSecretExpiresAt.ShouldBe(_clock.GetUtcNow().AddHours(2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(721)]
    public async Task SUB07_AnOverlapOutsideTheAllowedRangeIsRefusedWith400AndRotatesNothing(int hours)
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);

        var response = await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(CallbackUrl, true, hours), t.Product.ServiceToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StoredAsync(t, ct)).Previous.ShouldBeNull();
        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "the event arrives");
        SignaturesOf(_product.Requests[0]).ShouldBe([IndependentSignature(t.FirstSecret, long.Parse(_product.Requests[0].Headers["X-Auth-Timestamp"]), _product.Requests[0].Body)]);
    }

    [Fact]
    public async Task SUB07_TheFirstRegistrationHasNothingToOverlapAndAnEventHasOneSignature()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "the event arrives");

        SignaturesOf(_product.Requests[0]).Count.ShouldBe(1, "outside an overlap the header is exactly what it was before");
        (await ConfigAsync(t, ct)).PreviousSecretExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task SUB07_SecretsStayEncryptedAtRestAreNeverLoggedAndNeverReachTheAuditLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(ct);
        var rotation = await RotateAsync(t, overlapHours: null, ct);
        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "an event signed with both");

        var stored = await StoredAsync(t, ct);
        stored.Current.ShouldNotBeNullOrEmpty();
        stored.Previous.ShouldNotBeNullOrEmpty();
        foreach (var secret in new[] { t.FirstSecret, rotation.NewSecret })
        {
            stored.Current!.ShouldNotContain(secret);
            stored.Previous!.ShouldNotContain(secret);
            Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains(secret));
        }

        await WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.AsNoTracking().Where(entry => entry.Action == AuditActions.WebhookSecretRotated).ToListAsync(ct);
            var entry = entries.ShouldHaveSingleItem();
            entry.Detail.ShouldBe("overlap_hours_24");
            (entry.ActorType, entry.ActorId).ShouldBe((AuditActorType.Product, t.Product.Id));

            var everything = string.Join(' ', await db.AuditEntries.AsNoTracking().Select(candidate => candidate.Detail ?? string.Empty).ToListAsync(ct));
            everything.ShouldNotContain("whsec_");
        });
    }

    // ---- helpers ----

    private sealed record Setup(TestProduct Product, string FirstSecret);

    private sealed record Rotation(string NewSecret, DateTimeOffset? PreviousSecretExpiresAt);

    private async Task<Setup> SetUpAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var first = (await (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(CallbackUrl, null), product.ServiceToken, ct))
            .Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!;
        return new Setup(product, first.SigningSecret!);
    }

    private async Task<Rotation> RotateAsync(Setup t, int? overlapHours, CancellationToken ct)
    {
        var response = await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(CallbackUrl, true, overlapHours), t.Product.ServiceToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue("the response holds a secret");
        var body = (await response.Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!;
        return new Rotation(body.SigningSecret!, body.PreviousSecretExpiresAt);
    }

    private async Task<WebhookConfigResponse> ConfigAsync(Setup t, CancellationToken ct) =>
        (await (await GetAsync(WebhookConfigEndpoints.Route, t.Product.ServiceToken, ct)).Content.ReadFromJsonAsync<WebhookConfigResponse>(Json, ct))!;

    private async Task<(string? Current, string? Previous, DateTimeOffset? PreviousExpiresAt)> StoredAsync(Setup t, CancellationToken ct)
    {
        (string? Current, string? Previous, DateTimeOffset? PreviousExpiresAt) stored = default;
        await WithDbAsync(async db =>
        {
            var application = await db.Applications.AsNoTracking().SingleAsync(candidate => candidate.Id == t.Product.Id, ct);
            stored = (application.WebhookSecretProtected, application.WebhookPreviousSecretProtected, application.WebhookPreviousSecretExpiresAt);
        });
        return stored;
    }

    private async Task<Guid> SubscribeNewTenantAsync(Setup t, string tenantName, string email, CancellationToken ct)
    {
        var owner = await SignUpVerifiedAsync(tenantName, email, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, t.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return owner.TenantId;
    }

    /// <summary>The entries of the signature header, as a receiver reads them: split on commas.</summary>
    private static List<string> SignaturesOf(FakeWebhookReceiver.Received request) =>
        [.. request.Headers["X-Auth-Signature"].Split(',')];

    /// <summary>What a product's receiver does: recompute the signature with a secret it holds and accept if any entry matches.</summary>
    private static bool ReceiverAccepts(FakeWebhookReceiver.Received request, string secret) =>
        SignaturesOf(request).Contains(IndependentSignature(secret, long.Parse(request.Headers["X-Auth-Timestamp"]), request.Body));

    /// <summary>The signature as a product would compute it, written out here rather than by the service's own signer.</summary>
    private static string IndependentSignature(string secret, long timestamp, string body) =>
        "v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
}

/// <summary>The length of the overlap when a rotation does not ask for one is a setting, <c>Auth:Webhooks:SecretOverlapHours</c>.</summary>
public sealed class WebhookSecretOverlapSettingTests : AuthApiTestBase
{
    private const string CallbackUrl = "https://product.test/hooks";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));

    public WebhookSecretOverlapSettingTests(PostgresContainerFixture postgres)
        : base(postgres)
    {
    }

    protected override HarnessOptions Options => new()
    {
        Settings = { ["Auth:Webhooks:SecretOverlapHours"] = "3" },
        ConfigureServices = services => services.AddSingleton<TimeProvider>(_clock),
    };

    [Fact]
    public async Task SUB07_TheDefaultOverlapIsASetting()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(CallbackUrl, null), product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var rotated = (await (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(CallbackUrl, true), product.ServiceToken, ct))
            .Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!;

        rotated.PreviousSecretExpiresAt.ShouldBe(_clock.GetUtcNow().AddHours(3));
    }
}
