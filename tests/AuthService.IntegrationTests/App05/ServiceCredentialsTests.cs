using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AuthService.Api.Common;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.CurrentUser;
using AuthService.Api.Tokens;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AuthService.IntegrationTests.App05;

/// <summary>
/// APP-05 (AUTH-132) Product Client Credentials: a product trades its client ID and secret for a short-lived service token at
/// <c>POST /api/auth/token</c>, and a platform admin rotates the secret at
/// <c>POST /api/auth/admin/applications/{id}/secret/rotate</c> while the old one keeps working for a grace period.
/// Time is a controllable clock, so the 24-hour grace period is walked through, not waited for.
/// Test method names start with their Test Case ID (APP_TC12..APP_TC15) per the workbook convention.
/// </summary>
public sealed class ServiceCredentialsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    // Starts on a whole microsecond: the database stores microseconds, and secret expiry is compared exactly.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));

    protected override HarnessOptions Options => new()
    {
        Settings = { ["Auth:Applications:TokenIpLimit"] = TokenIpLimit.ToString() },
        ConfigureServices = services => services.AddSingleton<TimeProvider>(_clock),
    };

    // High enough that no test but the one about the limit meets it.
    private const int TokenIpLimit = 100;

    [Fact]
    public async Task APP_TC12_AProductRegistersAndGetsAServiceTokenWithItsClientIdAndSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);
        product.ClientSecret.ShouldNotBeNullOrEmpty("the secret is shown once, in the registration response");

        var response = await RequestTokenAsync(product.ClientId, product.ClientSecret, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue("a token response must never be cached");
        var token = (await response.Content.ReadFromJsonAsync<ServiceTokenResponse>(ct))!;
        token.TokenType.ShouldBe("Bearer");
        token.ExpiresIn.ShouldBe(10 * 60, "service tokens are short-lived");

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.AccessToken);
        jwt.Audiences.ShouldBe([IdentityTokenService.ServiceAudience]);
        jwt.GetClaim("app_id").Value.ShouldBe(product.Id.ToString());
        jwt.GetClaim("client_id").Value.ShouldBe(product.ClientId);
        jwt.GetClaim("token_use").Value.ShouldBe("service");
        jwt.Subject.ShouldBe("app:" + product.Id, "the subject is a product, never something that parses as a user id");
        jwt.ValidTo.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddMinutes(11));

        // The token opens the product's own record, and only that.
        var me = await GetAsync(CurrentApplicationEndpoints.Route, token.AccessToken, ct);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var self = (await me.Content.ReadFromJsonAsync<CurrentApplicationResponse>(Json, ct))!;
        (self.Id, self.Key, self.ClientId).ShouldBe((product.Id, "whatsapp-saas", product.ClientId));
    }

    [Fact]
    public async Task APP_TC12_TheCredentialsCanBeSentAsAnOAuthFormToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);

        var response = await Client.PostAsync(ServiceTokenEndpoints.Route, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = product.ClientId,
            ["client_secret"] = product.ClientSecret,
        }), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ServiceTokenResponse>(ct))!.AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task APP05_AServiceTokenNeverAddressesAnotherProductsRecord()
    {
        // The rule every product-side endpoint relies on (APP_TC13 itself is tested with the seat limit, SUB-04): the
        // product comes only from the verified token, so A's token can never address B.
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var productA = await RegisterApplicationAsync(adminToken, "product-a", ct);
        var productB = await RegisterApplicationAsync(adminToken, "product-b", ct);
        var tokenA = await ServiceTokenAsync(productA, ct);

        var me = await GetAsync($"{CurrentApplicationEndpoints.Route}?applicationId={productB.Id}", tokenA, ct);

        var self = (await me.Content.ReadFromJsonAsync<CurrentApplicationResponse>(Json, ct))!;
        self.Id.ShouldBe(productA.Id, "a request cannot ask for another product's record");
        self.Key.ShouldBe("product-a");
    }

    [Fact]
    public async Task APP_TC14_ARotatedSecretKeepsWorkingForTheGracePeriodAndThenStops()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);

        var rotate = await RotateAsync(adminToken, product.Id, ct);

        rotate.StatusCode.ShouldBe(HttpStatusCode.OK);
        rotate.Headers.CacheControl?.NoStore.ShouldBeTrue("the response holds a secret and must never be cached");
        var rotated = (await rotate.Content.ReadFromJsonAsync<RotateSecretResponse>(Json, ct))!;
        rotated.ClientSecret.ShouldNotBe(product.ClientSecret);
        rotated.ClientId.ShouldBe(product.ClientId, "rotation changes the secret, not the client id");
        rotated.PreviousSecretsExpireAt.ShouldBe(_clock.GetUtcNow().AddHours(24));

        // Within the grace period both secrets work.
        _clock.Advance(TimeSpan.FromHours(23));
        (await RequestTokenAsync(product.ClientId, product.ClientSecret, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RequestTokenAsync(product.ClientId, rotated.ClientSecret, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // After it only the new one does.
        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        var old = await RequestTokenAsync(product.ClientId, product.ClientSecret, ct);
        old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(old, ct)).Code.ShouldBe("invalid_client");
        (await RequestTokenAsync(product.ClientId, rotated.ClientSecret, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APP_TC15_AWrongClientSecretIsRejectedWith401()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);

        var response = await RequestTokenAsync(product.ClientId, "not-the-secret", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("invalid_client");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task APP05_AnUnknownClientGetsExactlyTheSameAnswerAsAWrongSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);

        var wrongSecret = await RequestTokenAsync(product.ClientId, "not-the-secret", ct);
        var unknownClient = await RequestTokenAsync("cl_doesnotexist", product.ClientSecret, ct);
        // Another product's secret with this product's client id is also just a wrong secret.
        var other = await RegisterApplicationAsync(adminToken, "other-product", ct);
        var crossed = await RequestTokenAsync(product.ClientId, other.ClientSecret, ct);

        foreach (var response in new[] { wrongSecret, unknownClient, crossed })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Compared without the per-request trace id, which differs by design.
        var bodies = await Task.WhenAll(new[] { wrongSecret, unknownClient, crossed }.Select(async response =>
        {
            var body = (await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(ct))!;
            body.Remove("traceId");
            return body.ToJsonString();
        }));
        bodies.Distinct().Count().ShouldBe(1, "the response must not reveal whether the client id exists");
    }

    [Fact]
    public async Task APP05_AProductThatIsStillInactiveCanGetAServiceToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);
        product.IsActive.ShouldBeFalse();

        // It needs the token to register its own catalog-related calls before it is ever offered to tenants.
        (await RequestTokenAsync(product.ClientId, product.ClientSecret, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null, "cl_x", "secret")]
    [InlineData("password", "cl_x", "secret")]
    [InlineData("Client_Credentials", "cl_x", "secret")]
    public async Task APP05_AnyGrantOtherThanClientCredentialsIsRejectedWith400(string? grantType, string clientId, string clientSecret)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostAsync(ServiceTokenEndpoints.Route,
            new { grant_type = grantType, client_id = clientId, client_secret = clientSecret }, null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("unsupported_grant_type");
    }

    [Theory]
    [InlineData("client_credentials", null, "secret")]
    [InlineData("client_credentials", "", "secret")]
    [InlineData("client_credentials", "cl_x", null)]
    [InlineData("client_credentials", "cl_x", "")]
    public async Task APP05_AMissingClientIdOrSecretIsRejectedWith400(string grantType, string? clientId, string? clientSecret)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostAsync(ServiceTokenEndpoints.Route,
            new { grant_type = grantType, client_id = clientId, client_secret = clientSecret }, null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("invalid_request");
    }

    [Fact]
    public async Task APP05_AMalformedOrUnsupportedBodyIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;

        var brokenJson = await Client.PostAsync(ServiceTokenEndpoints.Route, new StringContent("{not json", Encoding.UTF8, "application/json"), ct);
        var plainText = await Client.PostAsync(ServiceTokenEndpoints.Route, new StringContent("hello", Encoding.UTF8, "text/plain"), ct);
        var noBody = await Client.PostAsync(ServiceTokenEndpoints.Route, null, ct);

        brokenJson.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        plainText.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noBody.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task APP05_AServiceTokenIsRefusedOnIdentityAndAdminEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);
        var serviceToken = await ServiceTokenAsync(product, ct);

        (await GetAsync(CurrentUserEndpoints.Route, serviceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(ApplicationAdminEndpoints.Route, new { key = "x", name = "X", jwtAudience = "x-api" }, serviceToken, ct)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await RotateAsync(serviceToken, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a product cannot rotate its own secret");
    }

    [Fact]
    public async Task APP05_OnlyAServiceTokenOpensTheServiceEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);
        var productAudienceToken = CreateToken(admin.UserId, ServiceSigningKey(), TimeSpan.FromMinutes(5), audience: "whatsapp-saas-api");

        (await GetAsync(CurrentApplicationEndpoints.Route, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an identity token");
        (await GetAsync(CurrentApplicationEndpoints.Route, productAudienceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a product token");
        (await GetAsync(CurrentApplicationEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(CurrentApplicationEndpoints.Route, "garbage", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(CurrentApplicationEndpoints.Route, await ServiceTokenAsync(product, ct), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APP05_AServiceTokenStopsWorkingWhenItExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);
        var expired = CreateServiceToken(product, TimeSpan.FromMinutes(-5));

        (await GetAsync(CurrentApplicationEndpoints.Route, expired, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task APP05_ServiceTokenRequestsAreRateLimitedPerIpAddress()
    {
        var ct = TestContext.Current.CancellationToken;

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < TokenIpLimit + 1; attempt++)
        {
            statuses.Add((await RequestTokenAsync("cl_x", "secret", ct)).StatusCode);
        }

        statuses.Take(TokenIpLimit).ShouldAllBe(status => status == HttpStatusCode.Unauthorized);
        statuses[^1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task APP05_NeitherTheSecretNorTheClientIdIsLogged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);

        await RequestTokenAsync(product.ClientId, "a-wrong-secret-that-must-not-be-logged", ct);
        var rotated = (await (await RotateAsync(adminToken, product.Id, ct)).Content.ReadFromJsonAsync<RotateSecretResponse>(Json, ct))!;

        Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains("a-wrong-secret-that-must-not-be-logged"));
        Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains(product.ClientSecret));
        Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains(rotated.ClientSecret));
        Harness.Logs.Entries.ShouldContain(entry => entry.Contains("service token request was refused"));
    }

    [Fact]
    public async Task APP05_Rotation_OnlyHashesAreStoredAndTheRotationIsAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);

        var rotated = (await (await RotateAsync(adminToken, product.Id, ct)).Content.ReadFromJsonAsync<RotateSecretResponse>(Json, ct))!;

        await WithDbAsync(async db =>
        {
            var secrets = await db.ApplicationSecrets.AsNoTracking().OrderBy(secret => secret.CreatedAt).ToListAsync(ct);
            secrets.Count.ShouldBe(2);
            secrets[0].ExpiresAt.ShouldBe(rotated.PreviousSecretsExpireAt, "the old secret now has an end");
            secrets[1].ExpiresAt.ShouldBeNull("the new secret does not expire");
            secrets[1].SecretHash.ShouldBe(SecretToken.Hash(rotated.ClientSecret));
            secrets.Select(secret => secret.SecretHash).ShouldNotContain(rotated.ClientSecret);

            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.ApplicationSecretRotated, ct);
            entry.ActorId.ShouldBe(admin.UserId);
            entry.Detail.ShouldBe("whatsapp-saas");
        });
    }

    [Fact]
    public async Task APP05_Rotation_AnEmptyBodyUsesTheDefaultGracePeriod()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);
        var request = new HttpRequestMessage(HttpMethod.Post, RotateRoute(product.Id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var response = await Client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RotateSecretResponse>(Json, ct))!.PreviousSecretsExpireAt
            .ShouldBe(_clock.GetUtcNow().AddHours(24));
    }

    [Fact]
    public async Task APP05_Rotation_AGraceOfZeroCutsTheOldSecretOffAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);

        var rotated = (await (await RotateAsync(adminToken, product.Id, ct, graceHours: 0)).Content.ReadFromJsonAsync<RotateSecretResponse>(Json, ct))!;

        (await RequestTokenAsync(product.ClientId, product.ClientSecret, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RequestTokenAsync(product.ClientId, rotated.ClientSecret, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APP05_Rotation_RotatingAgainNeverExtendsAnOlderSecretsLife()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var first = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);
        var second = (await (await RotateAsync(adminToken, first.Id, ct, graceHours: 2)).Content.ReadFromJsonAsync<RotateSecretResponse>(Json, ct))!;

        // An hour later the product rotates again with the long default grace.
        _clock.Advance(TimeSpan.FromHours(1));
        var third = (await (await RotateAsync(adminToken, first.Id, ct)).Content.ReadFromJsonAsync<RotateSecretResponse>(Json, ct))!;

        // The first secret still ends two hours after the first rotation, not 24 hours after the second.
        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        (await RequestTokenAsync(first.ClientId, first.ClientSecret, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RequestTokenAsync(first.ClientId, second.ClientSecret, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the second is in its grace period");
        (await RequestTokenAsync(first.ClientId, third.ClientSecret, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APP05_Rotation_SimultaneousRotationsEachEndWithOneNeverExpiringSecretPerRotation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);

        var responses = await Task.WhenAll(
            RotateAsync(adminToken, product.Id, ct),
            RotateAsync(adminToken, product.Id, ct),
            RotateAsync(adminToken, product.Id, ct));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        await WithDbAsync(async db =>
        {
            var secrets = await db.ApplicationSecrets.AsNoTracking().ToListAsync(ct);
            secrets.Count.ShouldBe(4);
            secrets.Count(secret => secret.ExpiresAt is null).ShouldBe(1, "rotations are serialised, so only the last secret is current");
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(SecretRotationService.MaxGraceHours + 1)]
    public async Task APP05_Rotation_AnOutOfRangeGraceIsRejectedWith400AndChangesNothing(int graceHours)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);

        (await RotateAsync(adminToken, product.Id, ct, graceHours)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await WithDbAsync(async db => (await db.ApplicationSecrets.CountAsync(ct)).ShouldBe(1));
    }

    [Fact]
    public async Task APP05_Rotation_AnUnknownProductIsRejectedWith404()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await RotateAsync(adminToken, Guid.NewGuid(), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("application_not_found");
    }

    [Fact]
    public async Task APP05_Rotation_ANonAdminCannotRotate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var tenantToken = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;

        (await RotateAsync(tenantToken, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RotateAsync(null, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await WithDbAsync(async db => (await db.ApplicationSecrets.CountAsync(ct)).ShouldBe(1));
    }

    [Fact]
    public async Task APP05_RegistrationRefusesTheAudienceThatServiceTokensCarry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await PostAsync(ApplicationAdminEndpoints.Route,
            new { key = "sneaky", name = "Sneaky", jwtAudience = IdentityTokenService.ServiceAudience }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await WithDbAsync(async db => (await db.Applications.Where(application => !application.IsBuiltIn).CountAsync(ct)).ShouldBe(0));
    }

    private static string RotateRoute(Guid applicationId) => $"{ApplicationAdminEndpoints.Route}/{applicationId}/secret/rotate";

    private Task<HttpResponseMessage> RotateAsync(string? token, Guid applicationId, CancellationToken ct, int? graceHours = null) =>
        PostAsync(RotateRoute(applicationId), new RotateSecretRequest(graceHours), token, ct);

    private Task<HttpResponseMessage> RequestTokenAsync(string clientId, string clientSecret, CancellationToken ct) =>
        PostAsync(ServiceTokenEndpoints.Route,
            new { grant_type = ServiceTokenEndpoints.ClientCredentialsGrant, client_id = clientId, client_secret = clientSecret }, null, ct);

    private async Task<string> ServiceTokenAsync(RegisterApplicationResponse product, CancellationToken ct)
    {
        var response = await RequestTokenAsync(product.ClientId, product.ClientSecret, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ServiceTokenResponse>(ct))!.AccessToken;
    }

    private async Task<RegisterApplicationResponse> RegisterApplicationAsync(string token, string key, CancellationToken ct)
    {
        var response = await PostAsync(ApplicationAdminEndpoints.Route, new { key, name = key, jwtAudience = key + "-api" }, token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
    }

    /// <summary>A service token signed by the real key but issued at a chosen time, to test expiry without waiting.</summary>
    private string CreateServiceToken(RegisterApplicationResponse product, TimeSpan expiresIn)
    {
        using var scope = Harness.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IdentityTokenService>();
        return tokens.CreateServiceToken(product.Id, product.ClientId, DateTimeOffset.UtcNow + expiresIn - TimeSpan.FromMinutes(10)).Value;
    }
}
