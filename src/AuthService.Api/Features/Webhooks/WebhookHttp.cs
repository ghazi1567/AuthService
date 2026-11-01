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
            {
                var allowPrivate = provider.GetRequiredService<IOptions<WebhookOptions>>().Value.AllowPrivateAddresses;
                return new SocketsHttpHandler
                {
                    // A redirect could lead anywhere, including inside the network, so it is not followed: it counts as a failure.
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(5),

                    // Connects to the address the name resolves to *now* and refuses a private one, so a name that resolved to a
                    // public address when it was registered cannot later be pointed inside the network.
                    ConnectCallback = async (context, cancellationToken) =>
                    {
                        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                        var usable = addresses.FirstOrDefault(address => allowPrivate || !WebhookUrlValidator.IsPrivate(address))
                            ?? throw new HttpRequestException("The callback address is not a public address.");

                        var socket = new Socket(usable.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(usable, context.DnsEndPoint.Port), cancellationToken);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    },
                };
            });

        return services;
    }
}
