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

                // No cooldown between verification emails by default, so tests can ask for a new link straight away.
                ["Auth:EmailVerification:ResendCooldownSeconds"] = "0",

                // A generous per-IP login limit by default (every in-process request shares one address), so only the
                // tests about the limit ever hit it.
                ["Auth:LoginProtection:IpLimit"] = "1000",

                // Likewise for token exchange, limited per person: high unless a test is about the limit.
                ["Auth:TokenExchange:UserLimit"] = "1000",

                // The audit retention worker deletes old entries at startup and on a schedule. Off by default so a test that
                // plants old entries and calls the job itself cannot race the worker; the worker has its own test.
                ["Auth:Audit:RetentionEnabled"] = "false",

                // The webhook worker is off by default so queued events stay put for the tests that look at them; the tests about
                // delivery turn it on and swap in a fake receiver. Callbacks may be plain http, as a test product has no certificate.
                ["Auth:Webhooks:DispatcherEnabled"] = "false",
                ["Auth:Webhooks:DispatchIntervalMilliseconds"] = "50",
                ["Auth:Webhooks:AllowPrivateAddresses"] = "true",

                // Likewise for password reset: no cooldown between requests and a high per-IP limit by default.
                ["Auth:PasswordReset:RequestCooldownSeconds"] = "0",
                ["Auth:PasswordReset:IpLimit"] = "1000",
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
