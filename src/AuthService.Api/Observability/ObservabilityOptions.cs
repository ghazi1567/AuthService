using System.Net;

namespace AuthService.Api.Observability;

/// <summary>
/// Who may read <c>/metrics</c>, bound from <c>Observability</c>. The metrics say how busy the service is and where it is failing, which
/// is for the operator and not for the internet, so the endpoint answers only callers on the loopback address and on the networks
/// listed here (for example the monitoring server's range, <c>10.0.0.0/8</c>). Anyone else gets a plain 404.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Ranges allowed to read the metrics, in CIDR form. Loopback is always allowed.</summary>
    public string[] AllowedNetworks { get; set; } = [];

    /// <summary>A blank entry counts as not set: hosting platforms often pass an empty variable for a setting that is not used.</summary>
    private IEnumerable<string> Networks => AllowedNetworks.Where(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>Reasons the settings are unusable, so a typo stops the deployment instead of silently allowing nobody (or everybody).</summary>
    public IEnumerable<string> Problems()
    {
        foreach (var network in Networks)
        {
            if (!IPNetwork.TryParse(network, out _))
            {
                yield return $"Observability:AllowedNetworks has '{network}', which is not a range like 10.0.0.0/8.";
            }
        }
    }

    /// <summary>Whether a caller at this address may read the metrics. A connection with no address is never allowed.</summary>
    public bool IsAllowed(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) || Networks.Any(network => IPNetwork.Parse(network).Contains(address));
    }
}
