using System.Net;
using Microsoft.AspNetCore.Builder;

namespace AuthService.Api.Common;

/// <summary>
/// Which reverse proxies the service sits behind, bound from <c>Auth:Proxy</c>. Behind a proxy every connection comes from the
/// proxy's address, so per-IP rate limits would share one budget and audit entries would all name the proxy. When a proxy or network
/// is listed here, the client address is taken from <c>X-Forwarded-For</c>, but only when the connection really comes from a listed
/// proxy: from anyone else the header is ignored, so a caller cannot choose their own address. With nothing listed the header is
/// never read.
/// </summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Auth:Proxy";

    /// <summary>Addresses of trusted proxies, for example <c>10.0.0.5</c>.</summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>Ranges of trusted proxies in CIDR form, for example <c>10.0.0.0/8</c>.</summary>
    public string[] TrustedNetworks { get; set; } = [];

    /// <summary>
    /// How many <c>X-Forwarded-For</c> entries, counted from the right, are believed: the number of proxies in front of the service.
    /// 1 when a single load balancer is in front.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>A blank entry counts as not set: hosting platforms often pass an empty variable for a setting that is not used.</summary>
    private IEnumerable<string> Proxies => TrustedProxies.Where(value => !string.IsNullOrWhiteSpace(value));

    private IEnumerable<string> Networks => TrustedNetworks.Where(value => !string.IsNullOrWhiteSpace(value));

    public bool IsConfigured => Proxies.Any() || Networks.Any();

    /// <summary>Reasons the settings are unusable, so a typo stops the deployment instead of silently trusting nothing (or everything).</summary>
    public IEnumerable<string> Problems()
    {
        foreach (var address in Proxies)
        {
            if (!IPAddress.TryParse(address, out _))
            {
                yield return $"Auth:Proxy:TrustedProxies has '{address}', which is not an IP address.";
            }
        }

        foreach (var network in Networks)
        {
            if (!IPNetwork.TryParse(network, out _))
            {
                yield return $"Auth:Proxy:TrustedNetworks has '{network}', which is not a range like 10.0.0.0/8.";
            }
        }

        if (ForwardLimit is < 1 or > 10)
        {
            yield return "Auth:Proxy:ForwardLimit must be between 1 and 10.";
        }
    }

    /// <summary>The middleware settings. Only the client address is taken from the proxy, and only from the listed proxies.</summary>
    public ForwardedHeadersOptions ToForwardedHeadersOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor,
            ForwardLimit = ForwardLimit,
        };

        // The defaults trust loopback; only what is listed here is trusted.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var address in Proxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(address));
        }

        foreach (var network in Networks)
        {
            options.KnownIPNetworks.Add(IPNetwork.Parse(network));
        }

        return options;
    }
}
