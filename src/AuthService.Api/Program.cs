using AuthService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var authDbConnectionString = builder.Configuration.GetConnectionString("AuthDb")
    ?? throw new InvalidOperationException("Connection string 'AuthDb' is not configured.");

// Migrations are never applied on startup; they are applied explicitly (tests: TST-01 fixture).
builder.Services.AddAuthDbContext(authDbConnectionString);

var app = builder.Build();

app.MapGet("/", () => "AuthService");

app.Run();

// Exposed so integration tests (TST-02) can boot the API in-process via WebApplicationFactory<Program>.
public partial class Program;
