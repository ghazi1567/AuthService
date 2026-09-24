using AuthService.Infrastructure.Persistence;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// An isolated, already-migrated database cloned from the TST-01 template. Dropping happens on dispose.
/// TST-02 will point WebApplicationFactory's ConnectionStrings:AuthDb at <see cref="ConnectionString"/>.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly PostgresContainerFixture _owner;

    internal TestDatabase(PostgresContainerFixture owner, string name, string connectionString)
    {
        _owner = owner;
        Name = name;
        ConnectionString = connectionString;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public AuthDbContext CreateDbContext() => AuthDbContextRegistration.CreateAuthDbContext(ConnectionString);

    public async ValueTask DisposeAsync() => await _owner.DropDatabaseAsync(Name);
}
