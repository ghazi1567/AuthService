using System.Net;
using System.Net.Sockets;

namespace AuthService.Api.Features.Webhooks;

/// <summary>
/// Decides which callback URLs the service will call. A product registers its own URL, so without limits it could point the
/// service at addresses inside its host's network. In production the URL must be https and must not be (or resolve to) a private,
/// loopback or link-local address; <see cref="WebhookOptions.AllowPrivateAddresses"/> relaxes that for development and tests.
/// </summary>
public static class WebhookUrlValidator
{
    public const int MaxLength = 2000;

    /// <summary>The URL, or null with the reason it cannot be used.</summary>
    public static Uri? TryValidate(string? url, bool allowPrivateAddresses, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            error = $"Url must be an absolute URL of at most {MaxLength} characters.";
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowPrivateAddresses && uri.Scheme == Uri.UriSchemeHttp))
        {
            error = "Url must use https.";
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Url must not contain credentials or a fragment.";
            return null;
        }

        if (!allowPrivateAddresses && IsPrivateHost(uri))
        {
            error = "Url must point at a public address.";
            return null;
        }

        return uri;
    }

    /// <summary>True for names and addresses that lead into a private network. Checked again, on the real address, at delivery.</summary>
    private static bool IsPrivateHost(Uri uri)
    {
        var host = uri.IdnHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.') && !uri.HostNameType.Equals(UriHostNameType.IPv6))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IsPrivate(address);
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Link-local, unique-local (fc00::/7), site-local and multicast.
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            || bytes[0] == 0
            || bytes[0] >= 224;
    }
}
