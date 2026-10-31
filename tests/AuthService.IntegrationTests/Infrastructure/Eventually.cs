namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>Waits for something a background worker does, such as sending an email, without fixed sleeps.</summary>
public static class Eventually
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static async Task UntilAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        // One last look, so the failure message names what was being waited for.
        (await condition()).ShouldBeTrue($"Timed out waiting until {because}.");
    }

    public static Task UntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null) =>
        UntilAsync(() => Task.FromResult(condition()), because, timeout);
}
