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

    [Fact(Skip = "Enabled once TEN-02 (login) and TOK-01 (token exchange) exist.")]
    public Task TST_TC8_HarnessLogsInAndExchangesATokenThroughRealEndpoints() => Task.CompletedTask;

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
