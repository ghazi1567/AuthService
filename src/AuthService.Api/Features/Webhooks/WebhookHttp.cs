using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Webhooks;

/// <summary>The HTTP client that calls products. Tests replace its primary handler with a fake one.</summary>
public static class WebhookHttp
{
    public const string ClientName = "webhooks";

    public static IServiceCollection AddWebhookHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName)

            // The framework logs every request's URL. A product's callback URL can carry a secret (a token in its path), so none of
            // that logging is wanted here; the delivery service logs ids and outcomes instead.
            .RemoveAllLoggers()
            .ConfigureHttpClient((provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, provider.GetRequiredService<IOptions<WebhookOptions>>().Value.TimeoutSeconds)))
            .ConfigurePrimaryHttpMessageHandler(provider =>
                CreateHandler(provider.GetRequiredService<IOptions<WebhookOptions>>().Value.AllowPrivateAddresses));

        return services;
    }

    /// <summary>
    /// The handler that makes the calls. Public so a test can put the real handler (connect-time address check, no redirects, real TLS)
    /// in front of a real HTTPS receiver, changing only which certificate authority it trusts.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        // A redirect could lead anywhere, including inside the network, so it is not followed: it counts as a failure.
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),

        // Connects to the address the name resolves to *now* and refuses a private one, so a name that resolved to a
        // public address when it was registered cannot later be pointed inside the network.
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var usable = addresses.Where(address => allowPrivate || !WebhookUrlValidator.IsPrivate(address)).ToList();
            if (usable.Count == 0)
            {
                throw new HttpRequestException("The callback address is not a public address.");
            }

            // A name can have several addresses (IPv6 and IPv4), and the first may not be reachable: try them in turn.
            Exception? last = null;
            foreach (var address in usable)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException exception)
                {
                    socket.Dispose();
                    last = exception;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            throw new HttpRequestException("The callback address could not be reached.", last);
        },
    };
}
