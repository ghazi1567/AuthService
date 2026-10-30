namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// The single base class every API integration suite derives from (TST-02). Each test gets its own
/// running service and its own database copy, so tests cannot affect each other.
/// </summary>
/// <example>
/// <code>
/// public sealed class MyTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
/// {
///     [Fact]
///     public async Task TEN_TC1_Something() => (await Client.GetAsync("/", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
/// }
/// </code>
/// </example>
public abstract class AuthApiTestBase(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private AuthApiHarness? _harness;

    protected PostgresContainerFixture Postgres { get; } = postgres;

    protected AuthApiHarness Harness =>
        _harness ?? throw new InvalidOperationException("The test harness has not been started.");

    protected HttpClient Client => Harness.Client;

    protected TestDatabase Database => Harness.Database;

    public async ValueTask InitializeAsync() =>
        _harness = await AuthApiHarness.CreateAsync(Postgres, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}
