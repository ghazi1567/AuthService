using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AuthService.Api.Features.Jwks;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.IntegrationTests.Tok02;

/// <summary>
/// TOK-02 (AUTH-27) JWKS Endpoint, through the real <c>GET /.well-known/jwks.json</c>.
/// Test method names start with their Test Case ID (TOK_TC6) per the workbook convention.
/// </summary>
public sealed class JwksTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    [Fact]
    public async Task TOK_TC6_TheJwksDocumentIsPublicAndContainsTheCurrentSigningKey()
    {
        var ct = TestContext.Current.CancellationToken;

        // No Authorization header at all: the endpoint is public by design.
        var response = await GetAsync(JwksEndpoints.Route, bearerToken: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        response.Headers.CacheControl?.Public.ShouldBeTrue();
        response.Headers.CacheControl?.MaxAge.ShouldBe(TimeSpan.FromMinutes(5));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var key = document.RootElement.GetProperty("keys").EnumerateArray().ShouldHaveSingleItem();

        key.GetProperty("kty").GetString().ShouldBe("EC");
        key.GetProperty("crv").GetString().ShouldBe("P-256");
        key.GetProperty("use").GetString().ShouldBe("sig");
        key.GetProperty("alg").GetString().ShouldBe("ES256");
        key.GetProperty("kid").GetString().ShouldBe(ServiceSigningKey().KeyId, "it must be the key signing tokens right now");
        key.TryGetProperty("d", out _).ShouldBeFalse("a public document must never carry private key material");

        // A product can validate a real token with nothing but this document.
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var token = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;
        new JsonWebToken(token).Kid.ShouldBe(key.GetProperty("kid").GetString());

        using var publicKey = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Base64UrlEncoder.DecodeBytes(key.GetProperty("x").GetString()),
                Y = Base64UrlEncoder.DecodeBytes(key.GetProperty("y").GetString()),
            },
        });
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = "AuthService",
                ValidateAudience = false,
                IssuerSigningKey = new ECDsaSecurityKey(publicKey) { KeyId = key.GetProperty("kid").GetString() },
            });
        validation.IsValid.ShouldBeTrue(validation.Exception?.Message);
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TOK02_TheJwksDocumentHasExactlyTheStandardPublicFieldsForEachKey()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await GetAsync(JwksEndpoints.Route, bearerToken: null, ct);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        document.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["keys"]);
        document.RootElement.GetProperty("keys").EnumerateArray().First().EnumerateObject()
            .Select(property => property.Name)
            .ShouldBe(["kty", "crv", "use", "alg", "kid", "x", "y"], ignoreOrder: true);
    }

    [Fact]
    public async Task TOK02_ASignedTokenWhoseKeyIdIsNotPublishedIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        // Right issuer and unexpired, but signed by a key the service never published.
        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = CreateToken(account.UserId, new ECDsaSecurityKey(foreignKey) { KeyId = ServiceSigningKey().KeyId }, TimeSpan.FromMinutes(5));

        (await GetAsync("/api/auth/users/me", forged, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
