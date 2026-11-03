using System.Net;
using AuthService.Web.Api;

namespace AuthService.Web.Tests;

/// <summary>How the web app reads the API: values, problems, and every way a call can fail.</summary>
public sealed class AuthApiClientTests
{
    private static AuthApiClient ClientFor(FakeApiHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://auth-api.test") });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASuccessfulCallReturnsTheValue()
    {
        var client = ClientFor(FakeApiHandler.Json(HttpStatusCode.OK, "AuthService", "text/plain"));

        var result = await client.PingAsync(Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("AuthService");
        result.Problem.ShouldBeNull();
    }

    [Fact]
    public async Task AJsonResourceIsReadIntoItsType()
    {
        var client = ClientFor(FakeApiHandler.Json(HttpStatusCode.OK, """{"name":"Acme"}""", "application/json"));

        var result = await client.GetAsync<Tenant>("/api/auth/tenant/me", "a-token", Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Name.ShouldBe("Acme");
    }

    [Fact]
    public async Task AListIsReadPageByPageUntilThereIsNoNextCursor()
    {
        var handler = new FakeApiHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri!.Query switch
                {
                    "?limit=200" => """{"items":[{"name":"Acme"},{"name":"Globex"}],"next":{"after":"Globex","afterId":"6f9619ff-8b86-d011-b42d-00c04fc964ff"}}""",
                    "?limit=200&after=Globex&afterId=6f9619ff-8b86-d011-b42d-00c04fc964ff" => """{"items":[{"name":"Initech"}],"next":null}""",
                    _ => throw new InvalidOperationException("Unexpected request " + request.RequestUri),
                },
                System.Text.Encoding.UTF8,
                "application/json"),
        });

        var result = await ClientFor(handler).GetAllAsync<Tenant>("/api/auth/roles", "a-token", Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Select(tenant => tenant.Name).ShouldBe(["Acme", "Globex", "Initech"]);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ATimeCursorIsHandedBackWithItsTimestampEscaped()
    {
        var handler = new FakeApiHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri!.Query.Contains("before=", StringComparison.Ordinal)
                    ? """{"items":[{"name":"Old"}],"next":null}"""
                    : """{"items":[{"name":"New"}],"next":{"before":"2026-11-02T09:00:00+00:00","beforeId":"6f9619ff-8b86-d011-b42d-00c04fc964ff"}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });

        var result = await ClientFor(handler).GetAllAsync<Tenant>("/api/auth/apikeys?tenantId=1", "a-token", Token);

        result.Value!.Select(tenant => tenant.Name).ShouldBe(["New", "Old"]);
        handler.Requests[1].RequestUri!.Query.ShouldBe("?tenantId=1&limit=200&before=2026-11-02T09%3A00%3A00%2B00%3A00&beforeId=6f9619ff-8b86-d011-b42d-00c04fc964ff");
    }

    [Fact]
    public async Task AFailingPageFailsTheWholeListInsteadOfShowingHalfOfIt()
    {
        var calls = 0;
        var handler = new FakeApiHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[{"name":"Acme"}],"next":{"after":"Acme","afterId":"6f9619ff-8b86-d011-b42d-00c04fc964ff"}}""", System.Text.Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await ClientFor(handler).GetAllAsync<Tenant>("/api/auth/roles", "a-token", Token);

        result.IsSuccess.ShouldBeFalse();
        result.Problem!.Status.ShouldBe(500);
    }

    [Fact]
    public async Task ARateLimitBecomesAProblemWithItsCodeAndRetryAfter()
    {
        var handler = new FakeApiHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"status":429,"title":"Too many requests","detail":"Try again later.","code":"rate_limited"}""", System.Text.Encoding.UTF8, "application/problem+json"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        var result = await ClientFor(handler).PingAsync(Token);

        result.IsSuccess.ShouldBeFalse();
        result.Problem!.Status.ShouldBe(429);
        result.Problem.Code.ShouldBe("rate_limited");
        result.Problem.RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ValidationErrorsAreKeptPerField()
    {
        var client = ClientFor(FakeApiHandler.Json(
            HttpStatusCode.BadRequest,
            """{"status":400,"title":"One or more validation errors occurred.","errors":{"Email":["Email is required."],"Password":["Too short.","Too common."]}}"""));

        var result = await client.PingAsync(Token);

        result.Problem!.Errors!["Email"].ShouldBe(["Email is required."]);
        result.Problem.Errors["Password"].ShouldBe(["Too short.", "Too common."]);
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>", "text/html")]
    [InlineData("", "text/plain")]
    [InlineData("[1,2,3]", "application/json")]
    [InlineData("{not json", "application/json")]
    public async Task ABodyThatIsNotAProblemDocumentIsReportedWithoutLeakingIt(string body, string contentType)
    {
        var client = ClientFor(FakeApiHandler.Json(HttpStatusCode.BadGateway, body, contentType));

        var result = await client.PingAsync(Token);

        result.Problem!.Status.ShouldBe(502);
        result.Problem.Code.ShouldBe(ApiProblem.Unexpected);
        result.Problem.Detail.ShouldBeNull("what a proxy or crashed server sent is never shown to a person");
        result.Problem.Title.ShouldBeNull();
    }

    [Fact]
    public async Task ANetworkFailureIsReportedAsUnreachableNotThrown()
    {
        var client = ClientFor(FakeApiHandler.Throwing(new HttpRequestException("connection refused: secret-host:5432")));

        var result = await client.PingAsync(Token);

        result.Problem!.Code.ShouldBe(ApiProblem.Unreachable);
        result.Problem.Status.ShouldBe(503);
        result.Problem.Detail.ShouldBeNull("the exception text stays out of the result");
    }

    [Fact]
    public async Task TheClientsOwnTimeoutIsReportedAsUnreachable()
    {
        var client = ClientFor(FakeApiHandler.Throwing(new TaskCanceledException("The request timed out.")));

        var result = await client.PingAsync(Token);

        result.Problem!.Code.ShouldBe(ApiProblem.Unreachable);
    }

    [Fact]
    public async Task ACallerWhoGivesUpStillGetsTheCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var client = ClientFor(new FakeApiHandler(_ => throw new TaskCanceledException()));

        await Should.ThrowAsync<TaskCanceledException>(() => client.PingAsync(cancelled.Token));
    }

    [Fact]
    public async Task ASuccessWithAnUnreadableBodyIsReportedAsUnexpected()
    {
        var client = ClientFor(FakeApiHandler.Json(HttpStatusCode.OK, "{broken", "application/json"));

        var result = await client.GetAsync<Tenant>("/api/auth/tenant/me", null, Token);

        result.Problem!.Code.ShouldBe(ApiProblem.Unexpected);
        result.Problem.Status.ShouldBe(200);
    }

    [Fact]
    public async Task TheBearerTokenIsSentOnlyWhenOneIsGiven()
    {
        var handler = FakeApiHandler.Json(HttpStatusCode.OK, """{"name":"Acme"}""", "application/json");
        var client = ClientFor(handler);

        await client.GetAsync<Tenant>("/api/auth/tenant/me", "the-token", Token);
        await client.GetAsync<Tenant>("/api/auth/tenant/me", null, Token);

        handler.Requests[0].Headers.Authorization!.ToString().ShouldBe("Bearer the-token");
        handler.Requests[1].Headers.Authorization.ShouldBeNull();
    }

    private sealed record Tenant(string Name);
}
