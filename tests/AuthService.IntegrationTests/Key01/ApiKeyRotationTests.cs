using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.ApiKeys;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.IntegrationTests.Key01;

/// <summary>
/// KEY-02 (AUTH-31) rotation, walked through with a controllable clock instead of waiting 24 hours. Kept apart from the other API key
/// tests so only these move time. Test method names start with their Test Case ID (KEY_TC6, KEY_TC7) per the workbook convention.
/// </summary>
public sealed class ApiKeyRotationTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    // Starts on a whole microsecond: the database stores microseconds, and expiry is compared exactly.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));

    protected override HarnessOptions Options => new()
    {
        ConfigureServices = services => services.AddSingleton<TimeProvider>(_clock),
    };

    [Fact]
    public async Task KEY_TC6_RotatingAKeyReturnsANewRawKeyOnceWithTheSameScopeAndBothWorkDuringTheGracePeriod()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var original = await CreateKeyAsync(t, ct);

        var response = await PostAsync(ApiKeyEndpoints.RotateRouteFor(original.Key.Id), new { }, t.OwnerToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue("the response holds a key and must never be cached");
        var rotated = (await response.Content.ReadFromJsonAsync<RotatedBody>(Json, ct))!;
        rotated.RawKey.ShouldNotBe(original.RawKey);
        rotated.Key.Id.ShouldNotBe(original.Key.Id);
        (rotated.Key.Name, rotated.Key.ApplicationId, rotated.Key.Status).ShouldBe((original.Key.Name, original.Key.ApplicationId, "Active"));
        rotated.Key.Permissions.ShouldBe(original.Key.Permissions, "the same scope, exactly");
        rotated.PreviousKeyExpiresAt.ShouldBe(_clock.GetUtcNow().AddHours(24));

        _clock.Advance(TimeSpan.FromHours(23));
        (await ValidateAsync(t, original.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ValidateAsync(t, rotated.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var statuses = (await (await GetAsync(ApiKeyEndpoints.Route, t.OwnerToken, ct)).Content.ReadPageItemsAsync<ApiKeySummary>(Json, ct))!
            .ToDictionary(key => key.Id, key => key.Status);
        statuses[original.Key.Id].ShouldBe("Expiring");
        statuses[rotated.Key.Id].ShouldBe("Active");
        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.ApiKeyRotated, ct);
            (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((t.Owner.UserId, t.Owner.TenantId, rotated.Key.Id.ToString()));
        });
    }

    [Fact]
    public async Task KEY_TC7_TheOldKeyIsRefusedWith401OnceTheGracePeriodEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var original = await CreateKeyAsync(t, ct);
        var rotated = await RotateAsync(t, original.Key.Id, null, ct);

        _clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        (await ValidateAsync(t, original.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "still inside the 24 hours");

        _clock.Advance(TimeSpan.FromSeconds(1));
        var late = await ValidateAsync(t, original.RawKey, ct);

        late.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(late, ct)).Code.ShouldBe("invalid_api_key");
        (await ValidateAsync(t, rotated.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the new key carries on");
        (await ListAsync(t, ct)).Single(key => key.Id == original.Key.Id).Status.ShouldBe("Expired");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task KEY02_AGraceOfZeroCutsTheOldKeyOffAtOnceAndTheGraceCanBeChosen()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var first = await CreateKeyAsync(t, ct);
        var second = await CreateKeyAsync(t, ct);

        await RotateAsync(t, first.Key.Id, 0, ct);
        var withGrace = await RotateAsync(t, second.Key.Id, 2, ct);

        (await ValidateAsync(t, first.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a leaked key is cut off at once");
        _clock.Advance(TimeSpan.FromHours(1));
        (await ValidateAsync(t, second.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _clock.Advance(TimeSpan.FromHours(1));
        (await ValidateAsync(t, second.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ValidateAsync(t, withGrace.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task KEY02_RotatingAgainNeverExtendsAnOlderKeysLife()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var first = await CreateKeyAsync(t, ct);
        var second = await RotateAsync(t, first.Key.Id, 2, ct);

        _clock.Advance(TimeSpan.FromHours(1));
        var third = await RotateAsync(t, second.Key.Id, null, ct);
        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        (await ValidateAsync(t, first.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "still ends two hours after its own rotation");
        (await ValidateAsync(t, second.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "inside its own 24 hours");
        (await ValidateAsync(t, third.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task KEY02_ARevokedOrExpiredKeyCannotBeRotatedAndABadGraceIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var revoked = await CreateKeyAsync(t, ct);
        var expiring = await CreateKeyAsync(t, ct);
        var fine = await CreateKeyAsync(t, ct);
        await DeleteKeyAsync(t, revoked.Key.Id, ct);
        await RotateAsync(t, expiring.Key.Id, 1, ct);
        _clock.Advance(TimeSpan.FromHours(2));

        var revokedResponse = await PostAsync(ApiKeyEndpoints.RotateRouteFor(revoked.Key.Id), new { }, t.OwnerToken, ct);
        var expiredResponse = await PostAsync(ApiKeyEndpoints.RotateRouteFor(expiring.Key.Id), new { }, t.OwnerToken, ct);

        revokedResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(revokedResponse, ct)).Code.ShouldBe("api_key_not_active");
        expiredResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        foreach (var grace in new[] { -1, ApiKeyValidator.MaxGraceHours + 1 })
        {
            (await PostAsync(ApiKeyEndpoints.RotateRouteFor(fine.Key.Id), new RotateApiKeyRequest(grace), t.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await ValidateAsync(t, fine.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "a rejected rotation changes nothing");
    }

    [Fact]
    public async Task KEY02_RevokingARotatedOutKeyDuringItsGraceEndsItAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var original = await CreateKeyAsync(t, ct);
        var rotated = await RotateAsync(t, original.Key.Id, null, ct);

        await DeleteKeyAsync(t, original.Key.Id, ct);

        (await ValidateAsync(t, original.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ValidateAsync(t, rotated.RawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task KEY02_TwoSimultaneousRotationsEachProduceAWorkingKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var original = await CreateKeyAsync(t, ct);

        var responses = await Task.WhenAll(
            PostAsync(ApiKeyEndpoints.RotateRouteFor(original.Key.Id), new { }, t.OwnerToken, ct),
            PostAsync(ApiKeyEndpoints.RotateRouteFor(original.Key.Id), new { }, t.OwnerToken, ct));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Created);
        var keys = await Task.WhenAll(responses.Select(async response => (await response.Content.ReadFromJsonAsync<RotatedBody>(Json, ct))!.RawKey));
        keys.Distinct().Count().ShouldBe(2);
        foreach (var raw in keys)
        {
            (await ValidateAsync(t, raw, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    // ---- helpers ----

    private sealed record RotatedBody(ApiKeySummary Key, string RawKey, DateTimeOffset PreviousKeyExpiresAt);

    private sealed record Tenant(TestProduct Product, SignupResponse Owner, string OwnerToken);

    private async Task<Tenant> TenantAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Tenant(product, owner, await TenantTokenAsync((await LoginForTokensAsync("owner@acme.com", ct)).AccessToken, owner.TenantId, ct));
    }

    private async Task<CreatedApiKeyResponse> CreateKeyAsync(Tenant t, CancellationToken ct)
    {
        var response = await PostAsync(ApiKeyEndpoints.Route,
            new CreateApiKeyRequest($"Key {Guid.NewGuid():N}"[..12], t.Product.Id, [new ApiKeyPermissionInput("thing.use", "Full")]), t.OwnerToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(Json, ct))!;
    }

    private async Task<RotatedBody> RotateAsync(Tenant t, Guid keyId, int? graceHours, CancellationToken ct)
    {
        var response = await PostAsync(ApiKeyEndpoints.RotateRouteFor(keyId), new RotateApiKeyRequest(graceHours), t.OwnerToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RotatedBody>(Json, ct))!;
    }

    private Task<HttpResponseMessage> ValidateAsync(Tenant t, string key, CancellationToken ct) =>
        PostAsync(ApiKeyEndpoints.ValidateRoute, new ValidateApiKeyRequest(key, "thing.use", "Full"), t.Product.ServiceToken, ct);

    private async Task<List<ApiKeySummary>> ListAsync(Tenant t, CancellationToken ct) =>
        (await (await GetAsync(ApiKeyEndpoints.Route, t.OwnerToken, ct)).Content.ReadPageItemsAsync<ApiKeySummary>(Json, ct))!;

    private async Task DeleteKeyAsync(Tenant t, Guid keyId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, ApiKeyEndpoints.KeyRouteFor(keyId));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", t.OwnerToken);
        (await Client.SendAsync(request, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
