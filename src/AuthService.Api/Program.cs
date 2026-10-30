using AuthService.Api.Features.Signup;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

// Migrations are never applied on startup; they are applied explicitly (tests: TST-01 fixture).
// The connection string is read lazily so test overrides added after this point still apply.
builder.Services.AddAuthDbContext(serviceProvider =>
    serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("AuthDb")
        ?? throw new InvalidOperationException("Connection string 'AuthDb' is not configured."));

// An unhandled error becomes a plain 500 problem response, never a stack trace.
builder.Services.AddProblemDetails();

builder.Services.AddSingleton(TimeProvider.System);

// Salted PBKDF2 hashes; the hash is only ever stored, never returned by an API.
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services.AddScoped<SignupService>();

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/", () => "AuthService");
app.MapSignup();

app.Run();

// Exposed so integration tests (TST-02) can boot the API in-process via WebApplicationFactory<Program>.
public partial class Program;
