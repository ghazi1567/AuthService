using System.Net;
using Microsoft.AspNetCore.Builder;

namespace AuthService.Web.Security;

/// <summary>
/// Which reverse proxies the web app sits behind, bound from <c>AuthService:Proxy</c>. Behind a proxy that ends HTTPS, every connection
/// comes from the proxy and looks like plain http, so the app would see the wrong address and the wrong scheme. When a proxy or network is
/// listed here, the client address and scheme are taken from <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>, but only when the
/// connection really comes from a listed proxy: from anyone else the headers are ignored, so a visitor cannot choose their own address.
/// With nothing listed the headers are never read.
/// </summary>
public sealed class ProxyOptions
{
    public const string SectionName = "AuthService:Proxy";

    /// <summary>Addresses of trusted proxies, for example <c>10.0.0.5</c>.</summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>Ranges of trusted proxies in CIDR form, for example <c>172.29.200.0/24</c>.</summary>
    public string[] TrustedNetworks { get; set; } = [];

    /// <summary>How many <c>X-Forwarded-For</c> entries, counted from the right, are believed: the number of proxies in front of the app.</summary>
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
                yield return $"{SectionName}:TrustedProxies has '{address}', which is not an IP address.";
            }
        }

        foreach (var network in Networks)
        {
            if (!IPNetwork.TryParse(network, out _))
            {
                yield return $"{SectionName}:TrustedNetworks has '{network}', which is not a range like 172.29.200.0/24.";
            }
        }

        if (ForwardLimit is < 1 or > 10)
        {
            yield return $"{SectionName}:ForwardLimit must be between 1 and 10.";
        }
    }

    /// <summary>The middleware settings. Only the listed proxies are believed, and both the client address and the scheme are taken from them.</summary>
    public ForwardedHeadersOptions ToForwardedHeadersOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
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
