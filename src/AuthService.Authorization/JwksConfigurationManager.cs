using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Authorization;

/// <summary>
/// Keeps the Auth Service's public keys, so a token is checked here with no call to the Auth Service per request. The keys are fetched
/// once, refreshed on a timer, refreshed (at most every <see cref="AuthServiceAuthorizationOptions.MinimumRefreshSeconds"/>) when a token
/// names a key the product does not have, and the last good set is kept if a refresh fails. The JWT bearer handler takes it as its
/// configuration manager, which is how it already handles key changes for OpenID Connect.
/// </summary>
internal sealed class JwksConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly ConfigurationManager<JsonWebKeySet> _keys;
    private readonly TimeSpan _retryAfterFailure;
    private long _noFetchBeforeTicks;

    public JwksConfigurationManager(Uri jwksUrl, IDocumentRetriever retriever, int cacheMinutes, int minimumRefreshSeconds)
    {
        _retryAfterFailure = TimeSpan.FromSeconds(Math.Max(1, minimumRefreshSeconds));
        _keys = new ConfigurationManager<JsonWebKeySet>(jwksUrl.AbsoluteUri, new JwksRetriever(), retriever)
        {
            AutomaticRefreshInterval = TimeSpan.FromMinutes(Math.Max(1, cacheMinutes)),
            RefreshInterval = _retryAfterFailure,
        };
    }

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        // Before the first successful fetch there is no last good set to fall back on. Answer with no keys, so every token is refused
        // with a plain 401 rather than an error, and do not hammer an Auth Service that is down: try again after the minimum interval.
        if (Environment.TickCount64 < Interlocked.Read(ref _noFetchBeforeTicks))
        {
            return new OpenIdConnectConfiguration();
        }

        JsonWebKeySet set;
        try
        {
            set = await _keys.GetConfigurationAsync(cancel);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _noFetchBeforeTicks, Environment.TickCount64 + (long)_retryAfterFailure.TotalMilliseconds);
            return new OpenIdConnectConfiguration();
        }

        var configuration = new OpenIdConnectConfiguration { JsonWebKeySet = set };
        foreach (var key in set.GetSigningKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        return configuration;
    }

    public void RequestRefresh() => _keys.RequestRefresh();

    private sealed class JwksRetriever : IConfigurationRetriever<JsonWebKeySet>
    {
        public async Task<JsonWebKeySet> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel) =>
            new(await retriever.GetDocumentAsync(address, cancel));
    }
}

/// <summary>Fetches the key document through <see cref="IHttpClientFactory"/>, so a product controls the handler, proxy and timeouts.</summary>
internal sealed class JwksDocumentRetriever(IHttpClientFactory httpClientFactory) : IDocumentRetriever
{
    private const int MaxBytes = 256 * 1024;

    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        using var client = httpClientFactory.CreateClient(AuthServiceAuthorizationOptions.JwksHttpClientName);
        using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();

        // A key document is small. Refuse one that is not, rather than read an unbounded body.
        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new InvalidOperationException("The key document is larger than expected.");
        }

        var body = await response.Content.ReadAsStringAsync(cancel);
        return body.Length > MaxBytes ? throw new InvalidOperationException("The key document is larger than expected.") : body;
    }
}
