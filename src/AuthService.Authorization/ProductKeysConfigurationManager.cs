using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace AuthService.Authorization;

/// <summary>
/// The signing keys, for a product set up from the Auth Service's address: it waits for the Auth Service's issuer and address to be known
/// (reading them when the first token arrives, then keeping them), and from then on is the same key keeper as the other way of installing
/// the package. The JWT bearer handler asks for the keys before it judges a token, so by the time the issuer and audience are compared
/// they are known. With nothing known yet, it answers with no keys and every token gets a plain 401.
/// </summary>
internal sealed class ProductKeysConfigurationManager(
    ProductConnection connection,
    IHttpClientFactory httpClients,
    Microsoft.Extensions.Options.IOptions<AuthServiceProductOptions> options) : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly object _gate = new();
    private JwksConfigurationManager? _keys;
    private Uri? _keysUrl;

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        await connection.EnsureLoadedAsync(cancel);
        if (connection.Current is not { } settings)
        {
            return new OpenIdConnectConfiguration();
        }

        return await KeysFor(settings.JwksUrl).GetConfigurationAsync(cancel);
    }

    public void RequestRefresh()
    {
        lock (_gate)
        {
            _keys?.RequestRefresh();
        }
    }

    private JwksConfigurationManager KeysFor(Uri jwksUrl)
    {
        lock (_gate)
        {
            if (_keys is null || _keysUrl != jwksUrl)
            {
                var settings = options.Value;
                _keys = new JwksConfigurationManager(
                    jwksUrl,
                    new JwksDocumentRetriever(httpClients, AuthServiceProductOptions.HttpClientName),
                    settings.KeyCacheMinutes,
                    settings.MinimumRefreshSeconds);
                _keysUrl = jwksUrl;
            }

            return _keys;
        }
    }
}
