using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Web.Tests;

/// <summary>Starts the web app in-process with a fixed API address, and optionally a fake in place of the real API.</summary>
public sealed class WebAppFactory(HttpMessageHandler? fakeApi = null, string apiBaseUrl = "https://auth-api.test") : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("AuthService:Api:BaseUrl", apiBaseUrl);

        if (fakeApi is not null)
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<Api.AuthApiClient>().ConfigurePrimaryHttpMessageHandler(() => fakeApi));
        }
    }
}
