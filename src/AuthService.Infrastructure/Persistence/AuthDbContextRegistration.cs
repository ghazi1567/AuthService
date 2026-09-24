using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Infrastructure.Persistence;

/// <summary>
/// The single place the Auth Service database is configured. The API, the design-time factory
/// and the integration tests all go through here, so test and production configuration cannot drift.
/// </summary>
public static class AuthDbContextRegistration
{
    public static DbContextOptionsBuilder ConfigureAuthDb(this DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return options
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(AuthDbContext).Assembly.GetName().Name))
            .UseSnakeCaseNamingConvention();
    }

    public static IServiceCollection AddAuthDbContext(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddDbContext<AuthDbContext>(options => options.ConfigureAuthDb(connectionString));
    }

    /// <summary>Builds a standalone context (used by the design-time factory and the tests).</summary>
    public static AuthDbContext CreateAuthDbContext(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<AuthDbContext>();
        builder.ConfigureAuthDb(connectionString);
        return new AuthDbContext(builder.Options);
    }
}
