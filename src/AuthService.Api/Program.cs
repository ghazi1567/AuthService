using AuthService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Migrations are never applied on startup; they are applied explicitly (tests: TST-01 fixture).
// The connection string is read lazily so test overrides added after this point still apply.
builder.Services.AddAuthDbContext(serviceProvider =>
    serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("AuthDb")
        ?? throw new InvalidOperationException("Connection string 'AuthDb' is not configured."));

var app = builder.Build();

app.MapGet("/", () => "AuthService");

app.Run();

// Exposed so integration tests (TST-02) can boot the API in-process via WebApplicationFactory<Program>.
public partial class Program;
