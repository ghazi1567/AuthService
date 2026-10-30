using System.Diagnostics;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Tst01;

/// <summary>
/// TST-01 (AUTH-33) Ephemeral Test Database Provisioning.
/// Test method names start with their Test Case ID (TST_TC1..TST_TC5) per the workbook convention.
/// </summary>
public sealed class EphemeralDatabaseTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task TST_TC1_AllMigrationsAppliedAndNonePending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await fixture.CreateDatabaseAsync(ct);
        await using var context = database.CreateDbContext();

        var defined = context.Database.GetMigrations().ToList();
        var applied = (await context.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = (await context.Database.GetPendingMigrationsAsync(ct)).ToList();

        defined.ShouldNotBeEmpty("the Auth Service must ship at least the Baseline migration");
        applied.ShouldBe(defined);
        pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task TST_TC2_ModelHasNoChangesMissingAMigration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await fixture.CreateDatabaseAsync(ct);
        await using var context = database.CreateDbContext();

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "the EF Core model has changed without a migration; run 'dotnet ef migrations add <Name>'");
    }

    [Fact]
    public async Task TST_TC3_IsolatedDatabasesDoNotShareData()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var databaseA = await fixture.CreateDatabaseAsync(ct);
        await using var databaseB = await fixture.CreateDatabaseAsync(ct);

        databaseA.Name.ShouldNotBe(databaseB.Name);

        await PostgresContainerFixture.ExecuteAsync(
            databaseA.ConnectionString,
            "CREATE TABLE tc3_probe (id int NOT NULL); INSERT INTO tc3_probe (id) VALUES (1);",
            ct);

        var rowsInA = await PostgresContainerFixture.ScalarAsync(databaseA.ConnectionString, "SELECT count(*) FROM tc3_probe", ct);
        Convert.ToInt64(rowsInA).ShouldBe(1L);

        var tableInB = await PostgresContainerFixture.ScalarAsync(
            databaseB.ConnectionString, "SELECT to_regclass('public.tc3_probe') IS NOT NULL", ct);
        ((bool)tableInB!).ShouldBeFalse("data written to one test database must never be visible in another");
    }

    [Fact]
    public async Task TST_TC4_ServerMatchesPinnedVersionAndHasCitext()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await fixture.CreateDatabaseAsync(ct);

        var versionNum = await PostgresContainerFixture.ScalarAsync(database.ConnectionString, "SHOW server_version_num", ct);
        (int.Parse((string)versionNum!, System.Globalization.CultureInfo.InvariantCulture) / 10000)
            .ShouldBe(TestSettings.PostgresMajorVersion);

        var citext = await PostgresContainerFixture.ScalarAsync(
            database.ConnectionString, "SELECT count(*) FROM pg_extension WHERE extname = 'citext'", ct);
        Convert.ToInt64(citext).ShouldBe(1L, "the Baseline migration must install the citext extension");
    }

    [Fact]
    public async Task TST_TC5_DisposedContainerIsRemovedFromDocker()
    {
        var ct = TestContext.Current.CancellationToken;
        var container = PostgresContainerFixture.CreateContainer();
        var disposed = false;

        try
        {
            await container.StartAsync(ct);
            var containerId = container.Id;
            (await DockerContainerExistsAsync(containerId, ct)).ShouldBeTrue();

            await container.DisposeAsync();
            disposed = true;

            (await DockerContainerExistsAsync(containerId, ct)).ShouldBeFalse(
                "test containers must never persist after disposal");
        }
        finally
        {
            // If an assertion or the start failed before the explicit dispose, still remove the container.
            if (!disposed)
            {
                await container.DisposeAsync();
            }
        }
    }

    private static async Task<bool> DockerContainerExistsAsync(string containerId, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("container");
        startInfo.ArgumentList.Add("inspect");
        startInfo.ArgumentList.Add(containerId);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the docker CLI.");

        // Drain both streams so the process cannot block on a full pipe.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);

        return process.ExitCode == 0;
    }
}
