using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// TST-02: the Auth Service running in-process on its own isolated, already-migrated database,
/// plus an <see cref="HttpClient"/> that calls it. Dispose it to stop the service and drop the database.
/// Suites normally get one through <see cref="AuthApiTestBase"/>; create more only when a test needs
/// several independent instances (for example to prove they do not share data, or to try another configuration).
/// </summary>
public sealed class AuthApiHarness : IAsyncDisposable
{
    private readonly AuthApiFactory _factory;

    private AuthApiHarness(TestDatabase database, AuthApiFactory factory, CapturingLoggerProvider logs)
    {
        Database = database;
        Logs = logs;
        _factory = factory;
        Client = factory.CreateClient();
    }

    public TestDatabase Database { get; }

    /// <summary>Everything the service has logged so far.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>Talks to the service over HTTP, the same way a real caller would.</summary>
    public HttpClient Client { get; }

    public static async Task<AuthApiHarness> CreateAsync(
        PostgresContainerFixture postgres,
        CancellationToken cancellationToken,
        HarnessOptions? options = null)
    {
        var database = await postgres.CreateDatabaseAsync(cancellationToken);
        var logs = new CapturingLoggerProvider();
        var factory = new AuthApiFactory(database.ConnectionString, options ?? new HarnessOptions(), logs);

        try
        {
            return new AuthApiHarness(database, factory, logs);
        }
        catch
        {
            await factory.DisposeAsync();
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>A scope of the running service's own dependency-injection container.</summary>
    public AsyncServiceScope CreateScope() => _factory.Services.CreateAsyncScope();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await Database.DisposeAsync();
    }
}
