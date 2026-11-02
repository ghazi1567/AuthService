using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Web.Tests;

/// <summary>The web app as a whole: pages, headers, health and start-up checks. No database and no real API are involved.</summary>
public sealed class WebAppTests
{
    [Fact]
    public async Task TheHomePageRendersAsHtmlWithItsTitle()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("<h1>Auth Service</h1>");
        html.ShouldContain("<title>Auth Service</title>");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/healthz")]
    [InlineData("/no-such-page")]
    public async Task EveryResponseCarriesTheSecurityHeaders(string path)
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
        response.Headers.GetValues("Permissions-Policy").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task HtmlPagesAreNeverCached()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var page = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var health = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        page.Headers.CacheControl?.NoStore.ShouldBeTrue("a page can carry a token in its address or form state");
        health.Headers.CacheControl?.NoStore.ShouldNotBe(true, "only HTML is marked no-store");
    }

    [Fact]
    public async Task TheHealthCheckSaysOnlyThatTheAppIsRunning()
    {
        await using var factory = new WebAppFactory(FakeApiHandler.Throwing(new HttpRequestException("the API is down")));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("ok", "it does not depend on the API");
    }

    [Fact]
    public async Task AnUnknownPageShowsNotFoundWithoutAnyDetail()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/nothing/here", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("Page not found");
        html.ShouldNotContain("Exception", Case.Insensitive);
        html.ShouldNotContain("StackTrace", Case.Insensitive);
    }

    [Theory]
    [InlineData("ftp://auth-api.test")]
    [InlineData("auth-api.test")]
    [InlineData("/relative/path")]
    public async Task AnUnusableApiAddressStopsTheAppFromStarting(string address)
    {
        await using var factory = new WebAppFactory(apiBaseUrl: address);

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        Flatten(failure).ShouldContain("BaseUrl");
    }

    [Fact]
    public void AMissingApiAddressStopsTheAppFromStarting()
    {
        // Production does not load appsettings.Development.json, so no address is configured.
        using var factory = new WebAppFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("AuthService:Api:BaseUrl", null);
        });

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        Flatten(failure).ShouldContain("BaseUrl is required");
    }

    [Fact]
    public async Task ATimeoutOutsideTheAllowedRangeStopsTheAppFromStarting()
    {
        await using var factory = new WebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("AuthService:Api:TimeoutSeconds", "0"));

        var failure = Should.Throw<Exception>(() => configured.CreateClient());

        Flatten(failure).ShouldContain("TimeoutSeconds");
    }

    [Fact]
    public async Task TheApiClientUsesTheConfiguredAddress()
    {
        var fake = FakeApiHandler.Json(System.Net.HttpStatusCode.OK, "AuthService", "text/plain");
        await using var factory = new WebAppFactory(fake, "https://auth-api.test/");
        using var scope = factory.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<Api.AuthApiClient>().PingAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("AuthService");
        fake.Requests.Single().RequestUri.ShouldBe(new Uri("https://auth-api.test/"));
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            messages.Add(inner.Message);
        }

        return string.Join(" | ", messages);
    }
}
