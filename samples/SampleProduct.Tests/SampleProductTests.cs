using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AuthService.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SampleProduct.Tests;

/// <summary>
/// A product that installs the <c>AuthService.Authorization</c> package from a feed, with no project reference to the Auth Service, and
/// enforces tokens with it. These tests are what a product team would write; they pass against a published package or one packed in CI,
/// and so show that the package works for someone who only has the package. Tokens are made here and signed with a test key whose public
/// half is served as the JWKS, so no Auth Service needs to run.
/// </summary>
public sealed class SampleProductTests : IDisposable
{
    private const string Audience = "sample-product-api";

    private readonly ECDsaSecurityKey _signingKey = NewKey("test-key-1");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SampleProductTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient(AuthServiceAuthorizationOptions.JwksHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new JwksHandler(_signingKey))));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public void TheAuthorizationPackageIsTheInstalledPackageNotAProjectInTheRepository()
    {
        // The build records every library the app uses, and whether it came from a package or from a project.
        var depsFile = Path.Combine(AppContext.BaseDirectory, "SampleProduct.Tests.deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
        var libraries = deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(library => library.Name.StartsWith("AuthService.Authorization/", StringComparison.Ordinal))
            .ToList();

        var installed = libraries.ShouldHaveSingleItem();
        installed.Value.GetProperty("type").GetString().ShouldBe("package", "a project reference would be type 'project'");
        installed.Name.ShouldBe($"AuthService.Authorization/{ExpectedVersion}");
    }

    [Fact]
    public async Task ATokenWithTheRightAudienceAndPermissionIsAcceptedAndTheTenantIsTheTokens()
    {
        var tenant = Guid.NewGuid();
        var token = Token(tenant, ["campaign.read:ReadOnly"]);

        var response = await GetAsync("/campaigns?tenantId=" + Guid.NewGuid(), token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("tenantId").GetGuid()
            .ShouldBe(tenant, "the tenant comes from the token and never from the request");
    }

    [Fact]
    public async Task APermissionIsMetByItsLevelOrHigherAndNotByLess()
    {
        var tenant = Guid.NewGuid();

        (await PostAsync("/campaigns", Token(tenant, ["campaign.create:CreateRead"]))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync("/campaigns", Token(tenant, ["campaign.create:Full"]))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync("/campaigns", Token(tenant, ["campaign.create:ReadOnly"]))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AValidTokenWithoutThePermissionIsRefusedWith403()
    {
        var response = await GetAsync("/campaigns", Token(Guid.NewGuid(), ["something.else:Full"]));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ATokenForAnotherProductsAudienceIsRefusedWith401EvenWithAValidSignature()
    {
        var token = Token(Guid.NewGuid(), ["campaign.read:Full"], audience: "some-other-product-api");

        (await GetAsync("/campaigns", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnExpiredTokenAndATokenSignedWithAnUnknownKeyAreRefusedWith401()
    {
        var expired = Token(Guid.NewGuid(), ["campaign.read:Full"], expires: DateTime.UtcNow.AddHours(-1), notBefore: DateTime.UtcNow.AddHours(-2));
        var forged = Token(Guid.NewGuid(), ["campaign.read:Full"], key: NewKey("test-key-1"));

        (await GetAsync("/campaigns", expired)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync("/campaigns", forged)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "same key id, different key");
    }

    [Fact]
    public async Task NoTokenAndGarbageAreRefusedWith401AndNoExceptionTextLeaks()
    {
        var none = await GetAsync("/campaigns", null);
        var garbage = await GetAsync("/campaigns", "not.a.token");

        none.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        garbage.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await garbage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("Exception");
    }

    [Fact]
    public async Task ATokenWithNoTenantCannotReachATenantEndpoint()
    {
        var token = Token(null, ["campaign.read:Full"]);

        (await GetAsync("/me", token)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OnlyTheHealthEndpointIsOpenAndEveryOtherEndpointHasAPermissionOrTenantRule()
    {
        (await _client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // /me asks only for a tenant, on purpose; the audit looks for endpoints with no permission policy, so it is named as exempt.
        var open = await EndpointPolicyAudit.FindEndpointsWithoutPermissionPolicyAsync(
            _factory.Services, isExempt: endpoint => endpoint.RoutePattern.RawText is "/health" or "/me");

        open.ShouldBeEmpty();
    }

    // ---- helpers ----

    /// <summary>The version the test run was told to install; the restored package's folder carries it.</summary>
    private static string ExpectedVersion =>
        typeof(SampleProductTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "AuthorizationPackageVersion").Value!;

    private Task<HttpResponseMessage> GetAsync(string path, string? token) => SendAsync(HttpMethod.Get, path, token);

    private Task<HttpResponseMessage> PostAsync(string path, string? token) => SendAsync(HttpMethod.Post, path, token);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A product token as the Auth Service issues it: ES256, the key id in the header, and sub, tid, aud, iss and permissions.</summary>
    private string Token(
        Guid? tenant,
        string[] permissions,
        string audience = Audience,
        DateTime? expires = null,
        DateTime? notBefore = null,
        ECDsaSecurityKey? key = null)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()) };
        if (tenant is { } tenantId)
        {
            claims.Add(new Claim("tid", tenantId.ToString()));
        }

        claims.AddRange(permissions.Select(permission => new Claim("permissions", permission)));

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "AuthService",
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key ?? _signingKey, SecurityAlgorithms.EcdsaSha256),
        });
    }

    private static ECDsaSecurityKey NewKey(string keyId) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = keyId };

    /// <summary>Answers the package's request for the keys the way the Auth Service does: a JWKS with the public half of the test key.</summary>
    private sealed class JwksHandler(ECDsaSecurityKey key) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var parameters = key.ECDsa.ExportParameters(includePrivateParameters: false);
            var jwk = new
            {
                kty = "EC",
                crv = "P-256",
                use = "sig",
                alg = "ES256",
                kid = key.KeyId,
                x = Base64UrlEncoder.Encode(parameters.Q.X!),
                y = Base64UrlEncoder.Encode(parameters.Q.Y!),
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { keys = new[] { jwk } }), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
