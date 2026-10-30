using AuthService.Infrastructure.Persistence;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

// One PostgreSQL container for the whole test run (TST-01, AC2).
[assembly: AssemblyFixture(typeof(PostgresContainerFixture))]

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// TST-01: starts a throwaway PostgreSQL container once per test run, applies the Auth Service's
/// real EF Core migrations to a template database (AC1), and hands each test class its own
/// isolated copy of that database. The container is never reused and is removed on dispose;
/// the Testcontainers resource reaper removes it even if the test process crashes (AC2).
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    // CREATE DATABASE ... TEMPLATE is serialised so parallel test classes never clone at the same time.
    private static readonly SemaphoreSlim CloneLock = new(1, 1);

    private PostgreSqlContainer? _container;

    private PostgreSqlContainer Container =>
        _container ?? throw new InvalidOperationException("The PostgreSQL test container has not been started.");

    /// <summary>Connection string to the container's default "postgres" database (admin operations only).</summary>
    public string AdminConnectionString => Container.GetConnectionString();

    /// <summary>
    /// The single container definition used by the fixture (and by TST_TC5 to prove it is not persistent).
    /// Never add WithReuse(true): every test run must get a fresh instance.
    /// </summary>
    public static PostgreSqlContainer CreateContainer() =>
        new PostgreSqlBuilder(TestSettings.PostgresImage).Build();

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            _container = CreateContainer();
            await _container.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start the PostgreSQL test container ({TestSettings.PostgresImage}). " +
                "The Auth Service integration tests need a running Docker engine (Docker Desktop, Rancher or Podman).",
                ex);
        }

        await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE \"{TestSettings.TemplateDatabase}\"", cancellationToken);

        // AC1: the schema comes only from the real EF Core migrations, never from the model directly.
        await using (var context = AuthDbContextRegistration.CreateAuthDbContext(ConnectionStringFor(TestSettings.TemplateDatabase)))
        {
            await context.Database.MigrateAsync(cancellationToken);
        }

        // PostgreSQL refuses to clone a template database that still has open connections.
        NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Connection string for a named database inside the test container.</summary>
    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// Creates an isolated database cloned from the migrated template. Dispose it to drop it.
    /// </summary>
    public async Task<TestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"auth_{Guid.NewGuid():N}";

        await CloneLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteAsync(
                AdminConnectionString,
                $"CREATE DATABASE \"{name}\" TEMPLATE \"{TestSettings.TemplateDatabase}\"",
                cancellationToken);
        }
        finally
        {
            CloneLock.Release();
        }

        return new TestDatabase(this, name, ConnectionStringFor(name));
    }

    internal Task DropDatabaseAsync(string name) =>
        ExecuteAsync(AdminConnectionString, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", CancellationToken.None);

    internal static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<object?> ScalarAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
