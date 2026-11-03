using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthService.Web.Tests;

/// <summary>
/// Starts the web app in-process with a fixed API address, and optionally a fake in place of the real API, a fake clock, another
/// environment name, or a logger provider that records everything the app logs.
/// </summary>
public sealed class WebAppFactory(
    HttpMessageHandler? fakeApi = null,
    string apiBaseUrl = "https://auth-api.test",
    TimeProvider? time = null,
    string environment = "Development",
    ILoggerProvider? logs = null,
    IReadOnlyDictionary<string, string>? settings = null,
    bool peerFromHeader = false) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("AuthService:Api:BaseUrl", apiBaseUrl);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        if (logs is not null)
        {
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        }

        builder.ConfigureTestServices(services =>
        {
            if (fakeApi is not null)
            {
                services.AddHttpClient<Api.AuthApiClient>().ConfigurePrimaryHttpMessageHandler(() => fakeApi);
            }

            if (time is not null)
            {
                services.AddSingleton(time);
            }

            if (peerFromHeader)
            {
                // In-process requests have no address; the test says which peer a request comes from with X-Test-Peer.
                services.AddTransient<IStartupFilter, PeerStartupFilter>();
            }
        });
    }
}

/// <summary>Sets the connection's remote address from the <c>X-Test-Peer</c> header, as a real socket would.</summary>
internal sealed class PeerStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Peer";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var peer))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
            }

            await nextMiddleware();
        });
        next(app);
    };
}
