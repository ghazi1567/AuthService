using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Discovery;
using AuthService.IntegrationTests.Infrastructure;

namespace AuthService.IntegrationTests.Onb03;

/// <summary>ONB-03 (AUTH-274): how the combined client secret is read. No service is involved.</summary>
public sealed class ClientCredentialTests
{
    [Fact]
    public void ACredentialIsTheClientIdADotAndTheSecretAndReadsBackTheSame()
    {
        var credential = ClientCredential.Combine("cl_AbC-123_x", "the-secret_value-0");

        credential.ShouldBe("cl_AbC-123_x.the-secret_value-0");
        ClientCredential.TryParse(credential, out var clientId, out var secret).ShouldBeTrue();
        clientId.ShouldBe("cl_AbC-123_x");
        secret.ShouldBe("the-secret_value-0");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a-plain-secret-with-no-dot")]
    [InlineData("cl_abc.")]
    [InlineData("cl_.secret")]
    [InlineData(".secret")]
    [InlineData("abc.secret")]
    public void AnythingElseIsNotACredential(string? text)
    {
        ClientCredential.TryParse(text, out var clientId, out var secret).ShouldBeFalse();
        clientId.ShouldBeEmpty();
        secret.ShouldBeEmpty();
    }
}

/// <summary>
/// ONB-03 (AUTH-274) The one string a product team is given (client id, a dot, the secret) is shown at registration and when the
/// secret is replaced, and the token endpoint takes it, as well as the old pair. The tests have no workbook Test Case ID: the story's
/// acceptance criteria cover them.
/// </summary>
public sealed class ClientCredentialTokenTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task ONB03_RegistrationAndRotationBothGiveTheCombinedCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var registered = await RegisterAsync(adminToken, ct);
        registered.Credential.ShouldBe($"{registered.ClientId}.{registered.ClientSecret}");

        var rotation = await PostAsync($"{ApplicationAdminEndpoints.Route}/{registered.Id}/secret/rotate", new { graceHours = 1 }, adminToken, ct);
        rotation.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = (await rotation.Content.ReadFromJsonAsync<RotateSecretResponse>(Json, ct))!;
        rotated.ClientSecret.ShouldNotBe(registered.ClientSecret);
        rotated.Credential.ShouldBe($"{rotated.ClientId}.{rotated.ClientSecret}");
    }

    [Fact]
    public async Task ONB03_TheCredentialAloneIsEnoughToGetAServiceTokenAsJsonOrAsAForm()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterAsync(adminToken, ct);

        var asJson = await PostAsync(ServiceTokenEndpoints.Route, new { grant_type = "client_credentials", client_secret = product.Credential }, null, ct);
        var asForm = await Client.PostAsync(
            ServiceTokenEndpoints.Route,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_secret"] = product.Credential }),
            ct);

        asJson.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await asJson.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString().ShouldNotBeNullOrWhiteSpace();
        asJson.Headers.CacheControl!.NoStore.ShouldBeTrue();
        asForm.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ONB03_TheOldClientIdAndSecretPairAndTheCredentialWithItsOwnClientIdStillWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterAsync(adminToken, ct);

        var pair = await PostAsync(ServiceTokenEndpoints.Route, new { grant_type = "client_credentials", client_id = product.ClientId, client_secret = product.ClientSecret }, null, ct);
        var both = await PostAsync(ServiceTokenEndpoints.Route, new { grant_type = "client_credentials", client_id = product.ClientId, client_secret = product.Credential }, null, ct);

        pair.StatusCode.ShouldBe(HttpStatusCode.OK, "nothing that already works stops working");
        both.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ONB03_ACredentialBesideAnotherProductsClientIdIsRefusedLikeAnyWrongSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var first = await RegisterAsync(adminToken, ct, "first");
        var second = await RegisterAsync(adminToken, ct, "second");

        var response = await PostAsync(ServiceTokenEndpoints.Route, new { grant_type = "client_credentials", client_id = second.ClientId, client_secret = first.Credential }, null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("invalid_client");
    }

    [Fact]
    public async Task ONB03_AWrongSecretInsideACredentialIsRefusedAndABareSecretWithNoClientIdIsAnInvalidRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterAsync(adminToken, ct);

        var wrong = await PostAsync(ServiceTokenEndpoints.Route, new { grant_type = "client_credentials", client_secret = $"{product.ClientId}.not-the-secret" }, null, ct);
        var bare = await PostAsync(ServiceTokenEndpoints.Route, new { grant_type = "client_credentials", client_secret = product.ClientSecret }, null, ct);
        var empty = await PostAsync(ServiceTokenEndpoints.Route, new { grant_type = "client_credentials" }, null, ct);

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(wrong, ct)).Code.ShouldBe("invalid_client");
        bare.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(bare, ct)).Code.ShouldBe("invalid_request");
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ONB03_TheCredentialsTokenOnlyGrantsTheClientCredentialsGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await RegisterAsync(adminToken, ct);

        var response = await PostAsync(ServiceTokenEndpoints.Route, new { grant_type = "password", client_secret = product.Credential }, null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("unsupported_grant_type");
    }

    [Fact]
    public async Task ONB03_AProductReadsItsOwnAudienceAndTheKeysOfItsCatalogueInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var registered = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new
            {
                key = "marketing",
                name = "Marketing",
                jwtAudience = "marketing-api",
                permissions = new object[] { new { key = "campaign.send", label = "Send" }, new { key = "campaign.view", label = "View" }, new { key = "contact.view", label = "Contacts" } },
                activate = true,
            },
            adminToken,
            ct);
        var product = (await registered.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        var token = await PostAsync(ServiceTokenEndpoints.Route, new { grant_type = "client_credentials", client_secret = product.Credential }, null, ct);
        var serviceToken = (await token.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;

        var me = await GetAsync(CurrentApplicationEndpoints.Route, serviceToken, ct);

        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await me.Content.ReadFromJsonAsync<CurrentApplicationResponse>(Json, ct))!;
        body.Key.ShouldBe("marketing");
        body.JwtAudience.ShouldBe("marketing-api");
        body.IsActive.ShouldBeTrue();
        body.PermissionKeys.ShouldBe(["campaign.send", "campaign.view", "contact.view"]);
    }

    private async Task<RegisterApplicationResponse> RegisterAsync(string adminToken, CancellationToken ct, string key = "marketing")
    {
        var response = await PostAsync(ApplicationAdminEndpoints.Route, new { key, name = key, jwtAudience = key + "-api" }, adminToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
    }
}

/// <summary>
/// ONB-03 (AUTH-274) The discovery document: the one address a product starts from. Public, with no secret in it, and the issuer, the
/// keys and the token address agree with what the service really does.
/// </summary>
public sealed class DiscoveryTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    protected override HarnessOptions Options => new()
    {
        Settings = new Dictionary<string, string?>
        {
            ["Auth:Tokens:Issuer"] = "TestIssuer",
            ["Email:PublicBaseUrl"] = "https://app.example.com/",
        },
    };

    [Fact]
    public async Task ONB03_TheDocumentIsPublicAndNamesTheIssuerTheKeysTheTokenAddressAndTheConsole()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await GetAsync(DiscoveryEndpoints.Route, null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.Public.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromMinutes(5));
        var document = (await response.Content.ReadFromJsonAsync<DiscoveryDocument>(Json, ct))!;
        document.Issuer.ShouldBe("TestIssuer");
        document.ApiBaseUrl.ShouldBe("http://localhost", "with nothing configured, the address the request came to");
        document.JwksUrl.ShouldBe("http://localhost/.well-known/jwks.json");
        document.TokenUrl.ShouldBe("http://localhost/api/auth/token");
        document.ConsoleUrl.ShouldBe("https://app.example.com");
    }

    [Fact]
    public async Task ONB03_TheKeyAddressInTheDocumentReallyServesTheKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = (await (await GetAsync(DiscoveryEndpoints.Route, null, ct)).Content.ReadFromJsonAsync<DiscoveryDocument>(Json, ct))!;

        var keys = await Client.GetAsync(new Uri(document.JwksUrl).PathAndQuery, ct);

        keys.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await keys.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("keys").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ONB03_NothingSecretIsInTheDocument()
    {
        var ct = TestContext.Current.CancellationToken;

        var text = await (await GetAsync(DiscoveryEndpoints.Route, null, ct)).Content.ReadAsStringAsync(ct);

        using var json = JsonDocument.Parse(text);
        json.RootElement.EnumerateObject().Select(property => property.Name).Order().ShouldBe(["apiBaseUrl", "consoleUrl", "issuer", "jwksUrl", "tokenUrl"]);
    }
}

/// <summary>ONB-03 (AUTH-274) A configured public address wins over the address a request came to, so a proxy or an internal name never leaks into the document.</summary>
public sealed class DiscoveryPublicAddressTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    protected override HarnessOptions Options => new()
    {
        Settings = new Dictionary<string, string?> { ["Auth:Discovery:PublicBaseUrl"] = "https://auth.example.com/" },
    };

    [Fact]
    public async Task ONB03_TheConfiguredPublicAddressIsUsedWithoutATrailingSlash()
    {
        var ct = TestContext.Current.CancellationToken;

        var document = (await (await GetAsync(DiscoveryEndpoints.Route, null, ct)).Content.ReadFromJsonAsync<DiscoveryDocument>(Json, ct))!;

        document.ApiBaseUrl.ShouldBe("https://auth.example.com");
        document.JwksUrl.ShouldBe("https://auth.example.com/.well-known/jwks.json");
        document.TokenUrl.ShouldBe("https://auth.example.com/api/auth/token");
    }
}

/// <summary>ONB-03 (AUTH-274) A configured address that is not a web address is ignored, not trusted.</summary>
public sealed class DiscoveryBadPublicAddressTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    protected override HarnessOptions Options => new()
    {
        Settings = new Dictionary<string, string?> { ["Auth:Discovery:PublicBaseUrl"] = "not a url" },
    };

    [Fact]
    public async Task ONB03_ABadConfiguredAddressFallsBackToTheRequestsOwn()
    {
        var ct = TestContext.Current.CancellationToken;

        var document = (await (await GetAsync(DiscoveryEndpoints.Route, null, ct)).Content.ReadFromJsonAsync<DiscoveryDocument>(Json, ct))!;

        document.ApiBaseUrl.ShouldBe("http://localhost");
    }
}
