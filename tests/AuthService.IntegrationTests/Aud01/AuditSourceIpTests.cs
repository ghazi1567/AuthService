using System.Net;
using AuthService.Api.Features.Audit;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Aud01;

/// <summary>
/// AUD-01 (AUTH-134): each entry records the caller's IP address. In-process requests have no address, so this starts the
/// service with a fixed one. These tests have no workbook Test Case ID yet.
/// </summary>
public sealed class AuditSourceIpTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string CallerIp = "203.0.113.7";

    protected override HarnessOptions Options => new()
    {
        ConfigureServices = services => services.AddTransient<IStartupFilter, FixedRemoteIpStartupFilter>(),
    };

    [Fact]
    public async Task AUD01_EachEntryRecordsTheCallersIpAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        await LoginAsync("owner@acme.com", "not the password", ct);
        await LoginForTokensAsync("owner@acme.com", ct);

        await WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.AsNoTracking()
                .Where(entry => entry.Action == AuditActions.LoginFailed || entry.Action == AuditActions.LoginSucceeded || entry.Action == AuditActions.SignupCompleted)
                .ToListAsync(ct);

            entries.Count.ShouldBe(3);
            entries.ShouldAllBe(entry => entry.SourceIp == CallerIp);
        });
    }

    [Fact]
    public async Task AUD01_AnEntryMadeByTheServiceItselfHasNoAddress()
    {
        await WithDbAsync(async db =>
        {
            var created = await db.AuditEntries.AsNoTracking()
                .SingleAsync(entry => entry.Action == AuditActions.SigningKeyCreated, TestContext.Current.CancellationToken);

            created.SourceIp.ShouldBeNull("it happened at startup, not in a request");
        });
    }

    private sealed class FixedRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(CallerIp);
                await nextMiddleware();
            });
            next(app);
        };
    }
}
