using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real Auth Service in-process (TST-02) with its database pointed at one isolated test database.
/// The "Testing" environment is deliberate: it loads no appsettings.Development.json, so if the override
/// below ever stopped applying, startup would fail loudly instead of quietly using the localhost database.
/// </summary>
public sealed class AuthApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AuthDb"] = connectionString,
            }));
    }
}
