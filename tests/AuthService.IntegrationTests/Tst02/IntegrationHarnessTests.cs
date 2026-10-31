using System.Net;
using AuthService.Infrastructure.Persistence;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Tst02;

/// <summary>
/// TST-02 (AUTH-34) Integration Test Harness.
/// Test method names start with their Test Case ID (TST_TC6..TST_TC8) per the workbook convention.
/// </summary>
public sealed class IntegrationHarnessTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    [Fact]
    public async Task TST_TC6_ServiceBootsInProcessOnIsolatedDatabaseAndAnswersOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await Client.GetAsync("/", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe("AuthService");

        // The service must be using this test's own database copy, not the template or a shared one.
        Database.Name.ShouldNotBe(TestSettings.TemplateDatabase);

        await using var scope = Harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        context.Database.GetDbConnection().Database.ShouldBe(Database.Name);
        (await context.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty(
            "the service's database must be a fully migrated copy");
    }

    [Fact]
    public async Task TST_TC7_TwoHarnessesWritingAtTheSameTimeEachSeeOnlyTheirOwnData()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var other = await AuthApiHarness.CreateAsync(Postgres, ct);

        other.Database.Name.ShouldNotBe(Database.Name);

        // Same table name in both, written concurrently, with a different number of rows in each.
        await Task.WhenAll(WriteProbeRowsAsync(Harness, rows: 2, ct), WriteProbeRowsAsync(other, rows: 5, ct));

        (await CountProbeRowsAsync(Harness, ct)).ShouldBe(2L);
        (await CountProbeRowsAsync(other, ct)).ShouldBe(5L);
    }

    [Fact]
    public async Task TST_TC8_HarnessLogsInAndExchangesATokenThroughRealEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;

        // A product is set up by an admin and subscribes a tenant; the tenant's owner then logs in and trades the identity
        // session for a product token. Every step is a real HTTP call carrying a real token.
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        (await PostAsync($"/api/auth/tenants/{owner.TenantId}/subscriptions", new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var login = await LoginForTokensAsync("owner@acme.com", ct);

        var exchange = await PostAsync("/api/auth/token/exchange", new { tenantId = owner.TenantId, applicationId = product.Id }, login.AccessToken, ct);

        exchange.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await exchange.Content.ReadAsStringAsync(ct)).ShouldContain("accessToken");
    }

    private static async Task WriteProbeRowsAsync(AuthApiHarness harness, int rows, CancellationToken ct)
    {
        await using var scope = harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        await context.Database.ExecuteSqlRawAsync("CREATE TABLE tc7_probe (id int NOT NULL)", ct);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO tc7_probe (id) SELECT generate_series(1, {rows})", ct);
    }

    private static async Task<long> CountProbeRowsAsync(AuthApiHarness harness, CancellationToken ct)
    {
        await using var scope = harness.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await context.Database
            .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM tc7_probe")
            .SingleAsync(ct);
    }
}
