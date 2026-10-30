using Microsoft.EntityFrameworkCore.Design;

namespace AuthService.Infrastructure.Persistence;

/// <summary>
/// Used only by the dotnet-ef tool. "migrations add" never connects to the database, so the
/// fallback connection string only needs to be well-formed.
/// </summary>
public sealed class AuthDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    private const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=auth_design;Username=postgres;Password=postgres";

    public AuthDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("AUTH_DB_DESIGN_CONNECTION");

        return AuthDbContextRegistration.CreateAuthDbContext(
            string.IsNullOrWhiteSpace(connectionString) ? FallbackConnectionString : connectionString);
    }
}
