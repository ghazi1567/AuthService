namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// The only place the test database engine is pinned. It must match the production PostgreSQL
/// major version (Decision #5, AUTH-33). TST_TC4 fails if the running server does not match.
/// </summary>
public static class TestSettings
{
    public const string PostgresImage = "postgres:17-alpine";

    public const int PostgresMajorVersion = 17;

    /// <summary>Database the real EF Core migrations are applied to once per test run.</summary>
    public const string TemplateDatabase = "auth_template";
}
