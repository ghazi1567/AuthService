using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.TokenExchange;
using AuthService.IntegrationTests.Infrastructure;

namespace AuthService.IntegrationTests.Tok01;

/// <summary>
/// Token exchange is limited per signed-in person (and per IP address for a caller with no valid token), so a looping client
/// cannot hammer the database. These tests have no ticket: they close a known gap.
/// </summary>
public sealed class TokenExchangeRateLimitTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    protected override HarnessOptions Options => new()
    {
        Settings = new() { ["Auth:TokenExchange:UserLimit"] = "3" },
    };

    [Fact]
    public async Task APersonWhoExchangesTooOftenGets429WithRetryAfterAndNoToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, ana, _) = await TwoPeopleAsync(ct);
        var identity = (await LoginForTokensAsync("ana@acme.com", ct)).AccessToken;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await ExchangeAsync(identity, ana.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await ExchangeAsync(identity, ana.TenantId, product.Id, ct);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await ReadProblemAsync(limited, ct)).Code.ShouldBe("rate_limited");
        (await limited.Content.ReadAsStringAsync(ct)).ShouldNotContain("accessToken");
    }

    [Fact]
    public async Task AnotherPersonIsNotAffectedByTheFirstPersonsLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, ana, ben) = await TwoPeopleAsync(ct);
        var anaIdentity = (await LoginForTokensAsync("ana@acme.com", ct)).AccessToken;
        var benIdentity = (await LoginForTokensAsync("ben@other.com", ct)).AccessToken;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await ExchangeAsync(anaIdentity, ana.TenantId, product.Id, ct);
        }

        (await ExchangeAsync(anaIdentity, ana.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // They share an address (every in-process request does), so this proves the limit is per person.
        (await ExchangeAsync(benIdentity, ben.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OnlyExchangeIsLimited_AndEveryExchangeOfAPersonSharesOneBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, ana, _) = await TwoPeopleAsync(ct);
        var identity = (await LoginForTokensAsync("ana@acme.com", ct)).AccessToken;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await ExchangeAsync(identity, ana.TenantId, product.Id, ct);
        }

        (await GetAsync("/api/auth/users/me", identity, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ExchangeAsync(identity, ana.TenantId, AuthService.Api.Features.BuiltIn.BuiltInApplication.Id, ct)).StatusCode
            .ShouldBe(HttpStatusCode.TooManyRequests, "an exchange for the Auth Service's own product counts against the same budget");
    }

    [Fact]
    public async Task RefusedExchangesCountToo_SoGuessingTenantsIsLimited()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, _, _) = await TwoPeopleAsync(ct);
        var identity = (await LoginForTokensAsync("ana@acme.com", ct)).AccessToken;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await ExchangeAsync(identity, Guid.NewGuid(), product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await ExchangeAsync(identity, Guid.NewGuid(), product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ACallerWithNoValidTokenIsLimitedPerIpAddressInstead()
    {
        var ct = TestContext.Current.CancellationToken;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await ExchangeAsync(null, Guid.NewGuid(), Guid.NewGuid(), ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await ExchangeAsync(null, Guid.NewGuid(), Guid.NewGuid(), ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void TheDefaultIsGenerousForARealPerson()
    {
        var defaults = new TokenExchangeOptions();

        defaults.UserLimit.ShouldBe(30);
        defaults.UserWindowMinutes.ShouldBe(1);
    }

    private Task<HttpResponseMessage> ExchangeAsync(string? identityToken, Guid tenantId, Guid applicationId, CancellationToken ct) =>
        PostAsync(TokenExchangeEndpoints.Route, new { tenantId, applicationId }, identityToken, ct);

    /// <summary>One product, and two people who each own a tenant subscribed to it.</summary>
    private async Task<(TestProduct Product, AuthService.Api.Features.Signup.SignupResponse Ana, AuthService.Api.Features.Signup.SignupResponse Ben)> TwoPeopleAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var ana = await SignUpVerifiedAsync("Acme Ltd", "ana@acme.com", ct);
        var ben = await SignUpVerifiedAsync("Other Ltd", "ben@other.com", ct);
        foreach (var tenant in new[] { ana, ben })
        {
            (await PostAsync(SubscriptionEndpoints.RouteFor(tenant.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        return (product, ana, ben);
    }
}
