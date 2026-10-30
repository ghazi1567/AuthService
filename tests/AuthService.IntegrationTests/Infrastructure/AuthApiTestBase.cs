using System.Net.Http.Json;
using AuthService.Api.Features.Signup;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>Calls the real signup endpoint (TEN-01). Parameters are nullable so tests can send incomplete requests.</summary>
    protected Task<HttpResponseMessage> SignUpAsync(string? tenantName, string? email, string? password, CancellationToken ct) =>
        Client.PostAsJsonAsync(SignupEndpoints.Route, new { tenantName, email, password }, ct);

    /// <summary>
    /// Marks an email as verified directly in the database. Email verification (TEN-11) is not built yet, and login
    /// refuses unverified users, so tests that need a working login use this after signing up.
    /// </summary>
    protected async Task VerifyEmailAsync(string email, CancellationToken ct)
    {
        await using var scope = Harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var updated = await db.Users
            .Where(user => user.Email == email)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.EmailVerifiedAt, DateTimeOffset.UtcNow), ct);

        updated.ShouldBe(1, $"no user with email '{email}' to verify");
    }
}
