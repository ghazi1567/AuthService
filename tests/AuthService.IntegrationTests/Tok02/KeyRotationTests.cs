using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Api.Features.Jwks;
using AuthService.Api.Tokens;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.IntegrationTests.Tok02;

/// <summary>
/// TOK-02 (AUTH-27) key rotation, walked through with a controllable clock instead of waiting 90 days. Every test starts the
/// service at the same moment, so the first key's schedule is known exactly.
/// Test method names start with their Test Case ID (TOK_TC7) per the workbook convention.
/// </summary>
public sealed class KeyRotationTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    // Starts on a whole microsecond: the database stores microseconds, and rotation compares times exactly.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));

    protected override HarnessOptions Options => new()
    {
        ConfigureServices = services => services.AddSingleton<TimeProvider>(_clock),
    };

    [Fact]
    public async Task TOK_TC7_DuringARotationWindowTheNewAndThePreviousKeyAreBothPublishedUntilTheOverlapCloses()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstKid = CurrentKid();
        (await JwksKidsAsync(ct)).ShouldBe([firstKid]);

        // Day 89: the next key is published a full day before it starts signing. The first key still signs.
        _clock.Advance(TimeSpan.FromDays(89));
        await ReloadKeysAsync(ct);
        var kids = await JwksKidsAsync(ct);
        kids.Count.ShouldBe(2);
        kids[0].ShouldBe(firstKid);
        var secondKid = kids[1];
        CurrentKid().ShouldBe(firstKid, "the new key is published but not signing yet");

        // Day 90: the new key takes over. The previous key stays published, so tokens already issued still validate.
        _clock.Advance(TimeSpan.FromDays(1));
        CurrentKid().ShouldBe(secondKid);
        (await JwksKidsAsync(ct)).ShouldBe([firstKid, secondKid]);
        ValidationKeys(firstKid).ShouldNotBeEmpty("a token signed by the previous key must still validate during the overlap");
        ValidationKeys(secondKid).ShouldNotBeEmpty();

        // A day later the overlap closes and the previous key leaves the document.
        _clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        (await JwksKidsAsync(ct)).ShouldBe([secondKid]);
        ValidationKeys(firstKid).ShouldBeEmpty("after the overlap, tokens signed by the old key no longer validate");
        CurrentKid().ShouldBe(secondKid);
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TOK02_Rotation_NothingHappensBeforeTheWindowOpens()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstKid = CurrentKid();

        _clock.Advance(TimeSpan.FromDays(88));
        await ReloadKeysAsync(ct);

        (await JwksKidsAsync(ct)).ShouldBe([firstKid]);
        (await KeyCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task TOK02_Rotation_RepeatedReloadsScheduleTheNextKeyOnlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        _clock.Advance(TimeSpan.FromDays(89));

        await ReloadKeysAsync(ct);
        await ReloadKeysAsync(ct);
        await ReloadKeysAsync(ct);

        (await KeyCountAsync()).ShouldBe(2);
        (await JwksKidsAsync(ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task TOK02_Rotation_AnOverdueRotationStillWaitsTheFullPrePublishPeriod()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstKid = CurrentKid();

        // The service was down for a long time. The rotation is overdue, but no product may see a key it was not told about.
        _clock.Advance(TimeSpan.FromDays(200));
        await ReloadKeysAsync(ct);
        var kids = await JwksKidsAsync(ct);
        kids.Count.ShouldBe(2);
        CurrentKid().ShouldBe(firstKid, "the new key is only just published");

        _clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        CurrentKid().ShouldBe(firstKid, "still inside the pre-publish day");

        _clock.Advance(TimeSpan.FromMinutes(1));
        CurrentKid().ShouldBe(kids[1]);
    }

    [Fact]
    public async Task TOK02_Rotation_EmergencyRotationReplacesTheKeyAtOnceAndInvalidatesItsTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var oldToken = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;
        var oldKid = CurrentKid();
        (await GetAsync("/api/auth/users/me", oldToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await RotateNowAsync(ct);

        var newKid = CurrentKid();
        newKid.ShouldNotBe(oldKid);
        (await JwksKidsAsync(ct)).ShouldBe([newKid], "the old key leaves the document straight away");
        ValidationKeys(oldKid).ShouldBeEmpty();

        // A token signed by the old key stops working at once; a new login works.
        (await GetAsync("/api/auth/users/me", oldToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var newToken = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;
        (await GetAsync("/api/auth/users/me", newToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TOK02_Rotation_EmergencyRotationAlsoDiscardsAKeyThatWasPublishedForLater()
    {
        var ct = TestContext.Current.CancellationToken;
        _clock.Advance(TimeSpan.FromDays(89));
        await ReloadKeysAsync(ct);
        (await JwksKidsAsync(ct)).Count.ShouldBe(2);

        await RotateNowAsync(ct);

        var newKid = CurrentKid();
        (await JwksKidsAsync(ct)).ShouldBe([newKid]);

        // And the scheduled key does not come back when its turn would have been.
        _clock.Advance(TimeSpan.FromDays(2));
        (await JwksKidsAsync(ct)).ShouldBe([newKid]);
        CurrentKid().ShouldBe(newKid);
    }

    [Fact]
    public async Task TOK02_Rotation_WorkersRunningAtTheSameTimeOnAnEmptyTableCreateExactlyOneKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await PostgresContainerFixture.ExecuteAsync(Database.ConnectionString, "DELETE FROM signing_keys", ct);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var scope = Harness.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SigningKeyRotation>().EnsureAsync(_clock.GetUtcNow(), ct);
        }));

        (await KeyCountAsync()).ShouldBe(1, "a database lock must stop several instances each creating the first key");
    }

    [Fact]
    public async Task TOK02_Keys_ThePrivateKeyIsStoredEncryptedNeverAsPlainPkcs8()
    {
        await WithDbAsync(async db =>
        {
            var row = await db.SigningKeys.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

            using var ecdsa = ECDsa.Create();
            Should.Throw<CryptographicException>(
                () => ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(row.EncryptedPrivateKey), out _),
                "what is stored must not be a usable private key without the key-encryption secret");
        });
    }

    [Fact]
    public async Task TOK02_Keys_TheKeyIdIsTheRfc7638ThumbprintOfThePublicKey()
    {
        await WithDbAsync(async db =>
        {
            var row = await db.SigningKeys.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

            var thumbprint = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(
                $$"""{"crv":"P-256","kty":"EC","x":"{{row.PublicKeyX}}","y":"{{row.PublicKeyY}}"}""")));

            row.KeyId.ShouldBe(thumbprint, "the same public key must always have the same id");
        });
    }

    [Fact]
    public async Task TOK02_Rotation_ScheduledAndEmergencyRotationsAreAudited()
    {
        var ct = TestContext.Current.CancellationToken;

        _clock.Advance(TimeSpan.FromDays(89));
        await ReloadKeysAsync(ct);
        await RotateNowAsync(ct);

        await WithDbAsync(async db =>
        {
            var rotations = await db.AuditEntries.AsNoTracking()
                .Where(entry => entry.Action == "auth.signing_key.rotated")
                .OrderBy(entry => entry.OccurredAt)
                .ToListAsync(ct);

            rotations.Select(entry => entry.Detail).ShouldBe(["scheduled", "emergency"]);
            rotations.ShouldAllBe(entry => entry.ActorType == AuthService.Infrastructure.Persistence.Entities.AuditActorType.System);
        });
    }

    private string CurrentKid() => ServiceSigningKey().KeyId!;

    private async Task<List<string>> JwksKidsAsync(CancellationToken ct)
    {
        var response = await GetAsync(JwksEndpoints.Route, bearerToken: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("keys").EnumerateArray()
            .Select(key => key.GetProperty("kid").GetString()!)
            .ToList();
    }

    private List<SecurityKey> ValidationKeys(string kid)
    {
        using var scope = Harness.CreateScope();
        return [.. scope.ServiceProvider.GetRequiredService<SigningKeyService>().GetValidationKeys(kid)];
    }

    private async Task ReloadKeysAsync(CancellationToken ct)
    {
        await using var scope = Harness.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SigningKeyService>().RefreshAsync(ct);
    }

    private async Task RotateNowAsync(CancellationToken ct)
    {
        await using var scope = Harness.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SigningKeyService>().RotateNowAsync(ct);
    }

    private async Task<int> KeyCountAsync()
    {
        var count = 0;
        await WithDbAsync(async db => count = await db.SigningKeys.CountAsync(TestContext.Current.CancellationToken));
        return count;
    }
}
