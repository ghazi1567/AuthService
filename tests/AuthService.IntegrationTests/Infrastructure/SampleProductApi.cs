using System.Net;
using System.Security.Claims;
using AuthService.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// A tiny product API, hosted in the test process, that trusts the Auth Service only through the <c>AuthService.Authorization</c>
/// package, exactly as a real product would. It fetches the keys from the running Auth Service (through its in-process client), counts
/// how often, and can be told to fail fetching.
/// </summary>
public sealed class SampleProductApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly KeyFetchHandler _keyFetches;

    private SampleProductApi(WebApplication app, KeyFetchHandler keyFetches)
    {
        _app = app;
        _keyFetches = keyFetches;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>How many times this product has fetched the Auth Service's keys.</summary>
    public int KeyFetchCount => _keyFetches.Count;

    /// <summary>When true, fetching the keys fails, as if the Auth Service were down.</summary>
    public bool KeyFetchesFail
    {
        get => _keyFetches.Fail;
        set => _keyFetches.Fail = value;
    }

    public static async Task<SampleProductApi> StartAsync(
        HttpClient authServiceClient,
        string audience,
        Action<AuthServiceAuthorizationOptions>? configure = null,
        Action<WebApplication>? addEndpoints = null,
        TimeProvider? timeProvider = null)
    {
        var fetches = new KeyFetchHandler(authServiceClient);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (timeProvider is not null)
        {
            builder.Services.AddSingleton(timeProvider);
        }

        builder.Services.AddAuthServiceAuthorization(options =>
        {
            options.Issuer = "AuthService";
            options.Audience = audience;
            options.JwksUrl = new Uri("http://auth.test/.well-known/jwks.json");
            options.AllowInsecureJwksUrl = true;

            // Short, so a test that needs a refresh does not wait long. The default of 30 seconds is the real-world setting.
            options.MinimumRefreshSeconds = 1;
            configure?.Invoke(options);
        });
        builder.Services.AddHttpClient(AuthServiceAuthorizationOptions.JwksHttpClientName).ConfigurePrimaryHttpMessageHandler(() => fetches);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The tenant is only ever the token's. The query string and header below are decoys that must be ignored.
        app.MapGet("/campaigns", (ClaimsPrincipal user, HttpRequest request) => Results.Ok(new
        {
            tenantId = user.RequireTenantId(),
            decoyQuery = request.Query["tenantId"].ToString(),
            decoyHeader = request.Headers["X-Tenant-Id"].ToString(),
        })).RequirePermission("campaign.read", AccessLevel.ReadOnly);
        app.MapPost("/campaigns", (ClaimsPrincipal user) => Results.Ok(new { tenantId = user.RequireTenantId() }))
            .RequirePermission("campaign.create", AccessLevel.CreateRead);
        app.MapDelete("/campaigns/{id}", (ClaimsPrincipal user, string id) => Results.Ok(new { tenantId = user.RequireTenantId(), id }))
            .RequirePermission("campaign.delete", AccessLevel.Full);
        app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { tenantId = user.RequireTenantId(), userId = user.GetUserId() }))
            .RequireTenant();
        app.MapGet("/health", () => Results.Ok("ok")).AllowAnonymous();
        addEndpoints?.Invoke(app);

        await app.StartAsync();
        return new SampleProductApi(app, fetches);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>Sends the product's key requests to the running Auth Service, counting them.</summary>
    private sealed class KeyFetchHandler(HttpClient authServiceClient) : HttpMessageHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            if (Fail)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery);
            return await authServiceClient.SendAsync(forwarded, cancellationToken);
        }
    }
}
