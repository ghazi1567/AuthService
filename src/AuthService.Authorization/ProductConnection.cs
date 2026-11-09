using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthService.Authorization;

/// <summary>What was learned from the Auth Service about this product, and until when it is trusted without asking again.</summary>
internal sealed record ProductSettings(string Issuer, Uri JwksUrl, AuthServiceProduct Product, DateTimeOffset RefreshAt)
{
    public string Audience => Product.Audience;
}

/// <summary>
/// The product's link to the Auth Service (<c>AddAuthServiceProduct</c>): learns the issuer and the product's own audience from the
/// Auth Service's address and the client secret, and holds the service token the product's calls need. Every address it calls is built
/// from the one address the product was given, never from what the Auth Service answers, so a wrong answer cannot send the product's
/// secret or its key fetches somewhere else.
/// </summary>
internal sealed class ProductConnection(
    IOptions<AuthServiceProductOptions> options,
    IHttpClientFactory httpClients,
    TimeProvider time,
    ILogger<ProductConnection> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A token is asked for again this long before it expires, so a call never starts with one that is about to run out.
    private static readonly TimeSpan TokenMargin = TimeSpan.FromSeconds(60);

    private readonly AuthServiceProductOptions _options = options.Value;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private ProductSettings? _current;
    private (string Value, DateTimeOffset ExpiresAt)? _token;
    private long _noAttemptBeforeTicks;

    /// <summary>What is known now, or null if the Auth Service has never been reached successfully.</summary>
    public ProductSettings? Current => Volatile.Read(ref _current);

    public Uri Resolve(string path) =>
        new(new Uri(_options.Url!.AbsoluteUri.TrimEnd('/') + "/"), path.TrimStart('/'));

    /// <summary>
    /// Makes sure the issuer and audience are known and not stale. Quick when they are. If the Auth Service cannot be reached it does not
    /// throw: what was last known stays in use, and with nothing known every token is refused with a plain 401 until it can be reached.
    /// Another attempt waits for <see cref="AuthServiceProductOptions.MinimumRefreshSeconds"/>, so a stream of requests is not a stream of calls.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (IsFresh() || WaitingToRetry())
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh() || WaitingToRetry())
            {
                return;
            }

            try
            {
                Volatile.Write(ref _current, await LoadAsync(cancellationToken));
            }
            catch (Exception exception) when (exception is HttpRequestException or AuthServiceException or JsonException or InvalidOperationException
                or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Only the kind of failure, never a body: it could carry something that should not be in a log.
                logger.LogWarning(
                    "The Auth Service at {Url} could not be read ({Reason}). {Consequence}",
                    _options.Url,
                    exception is AuthServiceException { StatusCode: { } status } ? $"HTTP {(int)status}" : exception.GetType().Name,
                    Current is null ? "Tokens are refused until it can be." : "The last known settings stay in use.");
                Interlocked.Exchange(ref _noAttemptBeforeTicks, Environment.TickCount64 + (long)TimeSpan.FromSeconds(Math.Max(1, _options.MinimumRefreshSeconds)).TotalMilliseconds);
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>A service token, asked for when there is none or it is about to expire.</summary>
    public async Task<string> GetServiceTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached && cached.ExpiresAt - TokenMargin > time.GetUtcNow())
        {
            return cached.Value;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } again && again.ExpiresAt - TokenMargin > time.GetUtcNow())
            {
                return again.Value;
            }

            var fresh = await RequestTokenAsync(cancellationToken);
            _token = fresh;
            return fresh.Value;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>
    /// Sends a call as the product, with its service token. If the Auth Service turns the token down (for example after the secret was
    /// replaced), a new one is asked for and the call is made once more.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetServiceTokenAsync(cancellationToken));
            using var client = httpClients.CreateClient(AuthServiceProductOptions.HttpClientName);
            var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return response;
            }

            response.Dispose();
            _token = null;
        }
    }

    /// <summary>Turns a refusal into an exception that says what the Auth Service said, and nothing else from the response.</summary>
    public static async Task<AuthServiceException> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null, detail = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Json, cancellationToken);
            code = problem?.Code;
            detail = problem?.Detail ?? problem?.Title;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Not a problem document: the status alone has to do.
        }

        return new AuthServiceException(response.StatusCode, code, detail ?? $"The Auth Service answered {(int)response.StatusCode}.");
    }

    private bool IsFresh() => Current is { } settings && time.GetUtcNow() < settings.RefreshAt;

    private bool WaitingToRetry() => Environment.TickCount64 < Interlocked.Read(ref _noAttemptBeforeTicks);

    private async Task<ProductSettings> LoadAsync(CancellationToken cancellationToken)
    {
        using var client = httpClients.CreateClient(AuthServiceProductOptions.HttpClientName);

        using var discovery = await client.GetAsync(Resolve("/.well-known/auth-service-configuration"), cancellationToken);
        if (!discovery.IsSuccessStatusCode)
        {
            throw await ProblemAsync(discovery, cancellationToken);
        }

        var issuer = (await discovery.Content.ReadFromJsonAsync<DiscoveryBody>(Json, cancellationToken))?.Issuer;
        if (string.IsNullOrWhiteSpace(issuer))
        {
            throw new InvalidOperationException("The Auth Service did not say who issues its tokens.");
        }

        using var own = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Resolve("/api/auth/applications/me")), cancellationToken);
        if (!own.IsSuccessStatusCode)
        {
            throw await ProblemAsync(own, cancellationToken);
        }

        var record = await own.Content.ReadFromJsonAsync<ProductBody>(Json, cancellationToken)
            ?? throw new InvalidOperationException("The Auth Service sent an empty record.");
        if (string.IsNullOrWhiteSpace(record.JwtAudience))
        {
            throw new InvalidOperationException("The Auth Service did not say which audience is the product's own.");
        }

        var product = new AuthServiceProduct(
            record.Id, record.Key ?? "", record.Name ?? "", record.JwtAudience, record.ClientId ?? "", record.IsActive, record.PermissionKeys ?? []);
        return new ProductSettings(
            issuer.Trim(), Resolve("/.well-known/jwks.json"), product, time.GetUtcNow().AddMinutes(Math.Max(1, _options.ConfigurationCacheMinutes)));
    }

    private async Task<(string Value, DateTimeOffset ExpiresAt)> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials"), new("client_secret", _options.ClientSecret) };
        if (!string.IsNullOrWhiteSpace(_options.ClientId))
        {
            form.Add(new("client_id", _options.ClientId));
        }

        using var client = httpClients.CreateClient(AuthServiceProductOptions.HttpClientName);
        using var response = await client.PostAsync(Resolve("/api/auth/token"), new FormUrlEncodedContent(form), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ProblemAsync(response, cancellationToken);
        }

        var body = await response.Content.ReadFromJsonAsync<TokenBody>(Json, cancellationToken);
        if (string.IsNullOrEmpty(body?.AccessToken))
        {
            throw new InvalidOperationException("The Auth Service sent no service token.");
        }

        return (body.AccessToken, time.GetUtcNow().AddSeconds(Math.Max(0, body.ExpiresIn)));
    }

    private sealed record DiscoveryBody(string? Issuer);

    private sealed record ProductBody(Guid Id, string? Key, string? Name, string? JwtAudience, string? ClientId, bool IsActive, List<string>? PermissionKeys);

    private sealed record TokenBody(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record ProblemBody(string? Code, string? Title, string? Detail);
}
