using AuthService.Api.Email;
using AuthService.Api.Email.Providers;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Ntf01;

/// <summary>
/// NTF-01 (AUTH-135) Email Sending: provider failures and retries, switching provider by configuration, startup checks
/// and what the service logs. Each test starts the service with its own configuration.
/// Test method names start with their Test Case ID (NTF_TC2..NTF_TC4) per the workbook convention.
/// </summary>
public sealed class EmailProviderTests(PostgresContainerFixture postgres)
{
    private static HarnessOptions WithProvider(IEmailSender provider, params (string Key, string Value)[] settings) => new()
    {
        Settings = settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value),
        ConfigureServices = services => services.AddSingleton(provider),
    };

    // The signup variant of NTF_TC2 (signup still succeeds when the provider fails) arrives with TEN-11.
    [Fact]
    public async Task NTF_TC2_ProviderFailureDoesNotBlockTheRequestAndTheEmailIsRetriedAndLogged()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FlakyEmailSender(failFirst: 2);
        await using var harness = await AuthApiHarness.CreateAsync(postgres, ct, WithProvider(provider));

        // Queueing succeeds whatever the provider does: it only writes a row.
        await EmailTestSupport.EnqueueAsync(
            harness, EmailTemplate.Verification, "owner@acme.com", new() { ["token"] = "tok-1" }, ct);
        await EmailTestSupport.WaitUntilStatusAsync(harness, EmailOutboxStatus.Sent, 1, ct);

        provider.Attempts.ShouldBe(3, "two refusals, then one success");
        provider.Sent.ShouldHaveSingleItem();
        var row = (await EmailTestSupport.LoadOutboxAsync(harness, ct)).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(3);
        harness.Logs.Entries.Count(entry => entry.Contains("[Warning]") && entry.Contains("failed, will retry"))
            .ShouldBe(2, "each failed attempt is logged");
    }

    [Fact]
    public async Task NTF01_Retry_GivesUpAfterTheMaximumAttemptsAndClearsThePayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FlakyEmailSender(failFirst: int.MaxValue);
        await using var harness = await AuthApiHarness.CreateAsync(
            postgres, ct, WithProvider(provider, ("Email:MaxAttempts", "3")));

        await EmailTestSupport.EnqueueAsync(
            harness, EmailTemplate.Verification, "owner@acme.com", new() { ["token"] = "tok-1" }, ct);
        await EmailTestSupport.WaitUntilStatusAsync(harness, EmailOutboxStatus.Failed, 1, ct);

        provider.Attempts.ShouldBe(3);
        var row = (await EmailTestSupport.LoadOutboxAsync(harness, ct)).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(3);
        row.LastError.ShouldBe("Brevo returned 503.");
        row.PayloadJson.ShouldBeNull("the token must not stay in the database once the email is given up on");
        harness.Logs.Entries.ShouldContain(entry => entry.Contains("[Error]") && entry.Contains("failed for good"));

        // A failed email is not picked up again.
        await Task.Delay(300, ct);
        provider.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task NTF_TC3_SwitchingTheProviderByConfigurationNeedsNoCodeChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"auth-emails-{Guid.NewGuid():N}");

        try
        {
            await using var inMemory = await AuthApiHarness.CreateAsync(
                postgres, ct, new HarnessOptions { Settings = { ["Email:Provider"] = "InMemory" } });
            await using var file = await AuthApiHarness.CreateAsync(
                postgres,
                ct,
                new HarnessOptions { Settings = { ["Email:Provider"] = "File", ["Email:FileDirectory"] = directory } });

            await EmailTestSupport.EnqueueAsync(
                inMemory, EmailTemplate.Verification, "a@acme.com", new() { ["token"] = "memory-token" }, ct);
            await EmailTestSupport.EnqueueAsync(
                file, EmailTemplate.Verification, "b@acme.com", new() { ["token"] = "file-token" }, ct);
            await EmailTestSupport.WaitUntilStatusAsync(inMemory, EmailOutboxStatus.Sent, 1, ct);
            await EmailTestSupport.WaitUntilStatusAsync(file, EmailOutboxStatus.Sent, 1, ct);

            // Same code, same message, different provider chosen only by the setting.
            await using (var scope = inMemory.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<InMemoryEmailSender>().Sent
                    .ShouldHaveSingleItem().TextBody.ShouldContain("memory-token");
            }

            await using (var scope = file.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<InMemoryEmailSender>().Sent.ShouldBeEmpty();
            }

            var written = Directory.GetFiles(directory).ShouldHaveSingleItem();
            var content = await File.ReadAllTextAsync(written, ct);
            content.ShouldContain("To: b@acme.com");
            content.ShouldContain("file-token");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("Brevo", null, "no-reply@acme.com", "https://app.acme.com", "BrevoApiKey")]
    [InlineData("Brevo", "key-123", null, "https://app.acme.com", "FromAddress")]
    [InlineData("Brevo", "key-123", "no-reply@acme.com", null, "PublicBaseUrl")] // the default points at localhost
    [InlineData("Brevo", "key-123", "no-reply@acme.com", "http://app.acme.com", "PublicBaseUrl")] // a plain-http link carries the token in the clear
    [InlineData("Brevo", "key-123", "no-reply@acme.com", "app.acme.com", "PublicBaseUrl")] // not an absolute address
    [InlineData("Nonsense", null, null, null, "not supported")]
    public async Task NTF01_Startup_AnIncompleteOrUnknownProviderSettingStopsTheService(
        string provider, string? apiKey, string? fromAddress, string? publicBaseUrl, string expectedInMessage)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new HarnessOptions
        {
            Settings =
            {
                ["Email:Provider"] = provider,
                ["Email:BrevoApiKey"] = apiKey,
                ["Email:FromAddress"] = fromAddress,
                ["Email:PublicBaseUrl"] = publicBaseUrl,
            },
        };

        var failure = await Should.ThrowAsync<Exception>(AuthApiHarness.CreateAsync(postgres, ct, options));

        failure.ToString().ShouldContain(expectedInMessage);
    }

    [Fact]
    public async Task NTF01_Startup_AFullyConfiguredBrevoProviderStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new HarnessOptions
        {
            Settings =
            {
                ["Email:Provider"] = "Brevo",
                ["Email:BrevoApiKey"] = "key-123",
                ["Email:FromAddress"] = "no-reply@acme.com",
                ["Email:PublicBaseUrl"] = "https://app.acme.com",
            },
        };

        await using var harness = await AuthApiHarness.CreateAsync(postgres, ct, options);

        (await harness.Client.GetAsync("/", ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task NTF_TC4_LogsNeverContainTokenValuesLinksOrAddresses()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FlakyEmailSender(failFirst: 1); // include a failure in what gets logged
        await using var harness = await AuthApiHarness.CreateAsync(postgres, ct, WithProvider(provider));

        await EmailTestSupport.EnqueueAsync(
            harness, EmailTemplate.Verification, "owner@acme.com", new() { ["token"] = "SECRET-VERIFY-TOKEN-1" }, ct);
        await EmailTestSupport.EnqueueAsync(
            harness, EmailTemplate.PasswordReset, "owner@acme.com", new() { ["token"] = "SECRET-RESET-TOKEN-2" }, ct);
        await EmailTestSupport.WaitUntilStatusAsync(harness, EmailOutboxStatus.Sent, 2, ct);

        var logs = harness.Logs.Entries;

        // Not vacuous: the sends and the failure really were logged.
        logs.ShouldContain(entry => entry.Contains("sent after"));
        logs.ShouldContain(entry => entry.Contains("failed, will retry"));

        foreach (var forbidden in new[]
        {
            "SECRET-VERIFY-TOKEN-1",
            "SECRET-RESET-TOKEN-2",
            "verify-email?token",
            "reset-password?token",
            "https://app.test",
            "owner@acme.com",
        })
        {
            logs.ShouldNotContain(entry => entry.Contains(forbidden), $"logs must never contain '{forbidden}'");
        }
    }
}
