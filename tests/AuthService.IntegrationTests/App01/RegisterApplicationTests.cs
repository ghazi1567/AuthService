using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Common;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.App01;

/// <summary>
/// APP-01 (AUTH-15) Register Application (Product), through the real <c>POST /api/auth/admin/applications</c> with the access
/// token of a platform admin.
/// Test method names start with their Test Case ID (APP_TC1..APP_TC3) per the workbook convention.
/// </summary>
public sealed class RegisterApplicationTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task APP_TC1_APlatformAdminRegistersANewApplicationWithAUniqueKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await RegisterAsync(token, "whatsapp-saas", "WhatsApp SaaS", "whatsapp-saas-api", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue("the response holds a secret and must never be cached");
        var created = (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;

        created.Key.ShouldBe("whatsapp-saas");
        created.Name.ShouldBe("WhatsApp SaaS");
        created.JwtAudience.ShouldBe("whatsapp-saas-api");
        created.IsActive.ShouldBeFalse("a product is inactive until its permission catalog is registered");
        created.ClientId.ShouldStartWith("cl_");
        created.ClientSecret.ShouldNotBeNullOrEmpty();

        await WithDbAsync(async db =>
        {
            var application = await db.Applications.AsNoTracking().SingleAsync(ct);
            application.Id.ShouldBe(created.Id);
            application.IsActive.ShouldBeFalse();
            application.ClientId.ShouldBe(created.ClientId);

            // Only a hash of the secret is stored, so the database cannot reveal it.
            var secret = await db.ApplicationSecrets.AsNoTracking().SingleAsync(ct);
            secret.ApplicationId.ShouldBe(application.Id);
            secret.SecretHash.ShouldBe(SecretToken.Hash(created.ClientSecret));
            secret.SecretHash.ShouldNotBe(created.ClientSecret);
            secret.ExpiresAt.ShouldBeNull("the current secret does not expire");

            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.ApplicationRegistered, ct);
            entry.ActorId.ShouldBe(admin.UserId);
            entry.Detail.ShouldBe("whatsapp-saas");
        });
    }

    [Fact]
    public async Task APP_TC2_RegisteringAnApplicationWithAKeyThatAlreadyExistsIsRejectedWith409()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        (await RegisterAsync(token, "whatsapp-saas", "WhatsApp SaaS", "whatsapp-saas-api", ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var duplicate = await RegisterAsync(token, "whatsapp-saas", "Another Name", "another-audience", ct);
        var differentCase = await RegisterAsync(token, "WhatsApp-SaaS", "Another Name", "another-audience", ct);

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(duplicate, ct)).Code.ShouldBe("application_key_taken");
        differentCase.StatusCode.ShouldBe(HttpStatusCode.Conflict, "keys are case-insensitive");
        (await CountAsync()).ShouldBe((Applications: 1, Secrets: 1), "a refused registration creates nothing");
    }

    [Fact]
    public async Task APP_TC3_ANonAdminOrProductTokenCannotRegisterAnApplication()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUser = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var tenantToken = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;
        var productToken = CreateToken(tenantUser.UserId, ServiceSigningKey(), TimeSpan.FromMinutes(5), audience: "whatsapp-saas");

        (await RegisterAsync(tenantToken, "whatsapp-saas", "WhatsApp SaaS", "whatsapp-saas-api", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RegisterAsync(productToken, "whatsapp-saas", "WhatsApp SaaS", "whatsapp-saas-api", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RegisterAsync(null, "whatsapp-saas", "WhatsApp SaaS", "whatsapp-saas-api", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await CountAsync()).ShouldBe((Applications: 0, Secrets: 0));
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task APP01_ADuplicateJwtAudienceIsRejectedWith409()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        (await RegisterAsync(token, "whatsapp-saas", "WhatsApp SaaS", "shared-audience", ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await RegisterAsync(token, "other-product", "Other Product", "shared-audience", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("audience_taken");
        (await CountAsync()).Applications.ShouldBe(1);
    }

    [Fact]
    public async Task APP01_TheKeyIsTrimmedAndLowercased()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await RegisterAsync(token, "  WhatsApp-SaaS  ", "  WhatsApp SaaS  ", "  whatsapp-saas-api  ", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        created.Key.ShouldBe("whatsapp-saas");
        created.Name.ShouldBe("WhatsApp SaaS");
        created.JwtAudience.ShouldBe("whatsapp-saas-api");
    }

    [Fact]
    public async Task APP01_EachRegistrationGetsItsOwnClientIdAndSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var first = (await (await RegisterAsync(token, "product-one", "One", "audience-one", ct)).Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        var second = (await (await RegisterAsync(token, "product-two", "Two", "audience-two", ct)).Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;

        first.ClientId.ShouldNotBe(second.ClientId);
        first.ClientSecret.ShouldNotBe(second.ClientSecret);
    }

    [Fact]
    public async Task APP01_TheSecretIsNeverStoredOrLoggedInPlainText()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var created = (await (await RegisterAsync(token, "whatsapp-saas", "WhatsApp SaaS", "whatsapp-saas-api", ct)).Content
            .ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;

        await WithDbAsync(async db =>
        {
            var stored = JsonSerializer.Serialize(new
            {
                applications = await db.Applications.AsNoTracking().ToListAsync(ct),
                secrets = await db.ApplicationSecrets.AsNoTracking().ToListAsync(ct),
                audit = await db.AuditEntries.AsNoTracking().ToListAsync(ct),
            });
            stored.Contains(created.ClientSecret).ShouldBeFalse("the secret must exist only in the one response");
        });
        Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains(created.ClientSecret));
    }

    [Fact]
    public async Task APP01_SimultaneousRegistrationsOfTheSameKeyCreateExactlyOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var responses = await Task.WhenAll(
            RegisterAsync(token, "whatsapp-saas", "One", "audience-one", ct),
            RegisterAsync(token, "whatsapp-saas", "Two", "audience-two", ct),
            RegisterAsync(token, "whatsapp-saas", "Three", "audience-three", ct));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(2);
        (await CountAsync()).ShouldBe((Applications: 1, Secrets: 1));
    }

    [Theory]
    [InlineData(null, "Name", "audience")]
    [InlineData("   ", "Name", "audience")]
    [InlineData("has space", "Name", "audience")]
    [InlineData("-leading-hyphen", "Name", "audience")]
    [InlineData("trailing-hyphen-", "Name", "audience")]
    [InlineData("under_score", "Name", "audience")]
    [InlineData("key", null, "audience")]
    [InlineData("key", "   ", "audience")]
    [InlineData("key", "Name", null)]
    [InlineData("key", "Name", "has space")]
    [InlineData("key", "Name", "bad|char")]
    public async Task APP01_Validation_AnInvalidRequestIsRejectedWith400AndCreatesNothing(string? key, string? name, string? audience)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        (await RegisterAsync(token, key, name, audience, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await CountAsync()).Applications.ShouldBe(0);
    }

    [Fact]
    public async Task APP01_Validation_OverlongValuesAreRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        (await RegisterAsync(token, new string('a', ApplicationValidator.MaxKeyLength + 1), "Name", "audience", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RegisterAsync(token, "key", new string('n', ApplicationValidator.MaxNameLength + 1), "audience", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RegisterAsync(token, "key", "Name", new string('x', ApplicationValidator.MaxAudienceLength + 1), ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await RegisterAsync(token, new string('a', ApplicationValidator.MaxKeyLength), "Name", "audience", ct)).StatusCode
            .ShouldBe(HttpStatusCode.Created, "a key exactly at the limit is fine");
    }

    private Task<HttpResponseMessage> RegisterAsync(string? token, string? key, string? name, string? jwtAudience, CancellationToken ct) =>
        PostAsync(ApplicationAdminEndpoints.Route, new { key, name, jwtAudience }, token, ct);

    private async Task<(int Applications, int Secrets)> CountAsync()
    {
        var counts = (Applications: 0, Secrets: 0);
        await WithDbAsync(async db => counts = (
            await db.Applications.CountAsync(TestContext.Current.CancellationToken),
            await db.ApplicationSecrets.CountAsync(TestContext.Current.CancellationToken)));
        return counts;
    }
}
