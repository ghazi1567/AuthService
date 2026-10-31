using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real Auth Service in-process (TST-02) with its database pointed at one isolated test database.
/// The "Testing" environment is deliberate: it loads no appsettings.Development.json, so if the override
/// below ever stopped applying, startup would fail loudly instead of quietly using the localhost database.
/// </summary>
public sealed class AuthApiFactory(string connectionString, HarnessOptions options, CapturingLoggerProvider logs)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:AuthDb"] = connectionString,

                // Email is delivered by a background worker; poll fast and retry at once so tests do not wait.
                ["Email:DispatchIntervalMilliseconds"] = "50",
                ["Email:RetryDelaySeconds:0"] = "0",
                ["Email:PublicBaseUrl"] = "https://app.test",
            };

            foreach (var (key, value) in options.Settings)
            {
                settings[key] = value;
            }

            configuration.AddInMemoryCollection(settings);
        });

        builder.ConfigureLogging(logging =>
        {
            // Only the capturing provider: tests stay quiet, and it sees everything, whatever the config rules say.
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(logs);
            logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
        });

        builder.ConfigureTestServices(services => options.ConfigureServices?.Invoke(services));
    }
}
