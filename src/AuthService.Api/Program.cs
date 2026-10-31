using System.Text.Json.Serialization;
using AuthService.Api.Email;
using AuthService.Api.Email.Providers;
using AuthService.Api.Features.Login;
using AuthService.Api.Features.Logout;
using AuthService.Api.Features.Refresh;
using AuthService.Api.Features.Signup;
using AuthService.Api.Tokens;
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

// Enums are written by name ("Active"), not number.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<IdentityTokenOptions>(builder.Configuration.GetSection(IdentityTokenOptions.SectionName));
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddSingleton<IdentityTokenService>();

// Email (NTF-01): queued in the database, sent by a background worker through whichever provider is configured.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddHttpClient(BrevoEmailSender.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.brevo.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<InMemoryEmailSender>();
builder.Services.AddSingleton<IEmailSender>(EmailSenderFactory.Create);
builder.Services.AddScoped<EmailOutbox>();
builder.Services.AddScoped<EmailDeliveryService>();
builder.Services.AddHostedService<EmailDispatcher>();

builder.Services.AddScoped<SignupService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<RefreshService>();
builder.Services.AddScoped<LogoutService>();

var app = builder.Build();

// Create the signing key now so a missing key stops the deployment instead of failing the first login.
app.Services.GetRequiredService<SigningKeyProvider>();

// Likewise, an unsupported or incomplete email provider setting stops the deployment instead of failing the first email.
app.Services.GetRequiredService<IEmailSender>();

app.UseExceptionHandler();

app.MapGet("/", () => "AuthService");
app.MapSignup();
app.MapLogin();
app.MapRefresh();
app.MapLogout();

app.Run();

// Exposed so integration tests (TST-02) can boot the API in-process via WebApplicationFactory<Program>.
public partial class Program;
