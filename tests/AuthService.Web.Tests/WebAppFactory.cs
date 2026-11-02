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
    ILoggerProvider? logs = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("AuthService:Api:BaseUrl", apiBaseUrl);

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
        });
    }
}
