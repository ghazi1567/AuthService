using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AuthService.Api.Common;
using AuthService.Api.Email;
using AuthService.Api.Email.Providers;
using AuthService.Api.Features.CurrentUser;
using AuthService.Api.Features.EmailVerification;
using AuthService.Api.Features.Login;
using AuthService.Api.Features.Logout;
using AuthService.Api.Features.PasswordReset;
using AuthService.Api.Features.Refresh;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Tenants;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

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

// Bearer authentication for identity tokens (TEN-06 onwards).
builder.Services.AddIdentityTokenAuthentication();

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

// Login protection (TEN-12).
builder.Services.Configure<LoginProtectionOptions>(builder.Configuration.GetSection(LoginProtectionOptions.SectionName));

// Password reset (TEN-10).
builder.Services.Configure<PasswordResetOptions>(builder.Configuration.GetSection(PasswordResetOptions.SectionName));
builder.Services.AddScoped<PasswordResetService>();

// Email verification (TEN-11).
builder.Services.Configure<EmailVerificationOptions>(builder.Configuration.GetSection(EmailVerificationOptions.SectionName));
builder.Services.AddScoped<EmailVerificationService>();

// Limits how often one IP address can ask for verification emails. (Behind a proxy this sees the proxy's address until
// forwarded headers are configured for the deployment; TEN-12 revisits rate limiting.)
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.OnRejected = async (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await ApiProblem.Create(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests", "Try again later.")
            .ExecuteAsync(context.HttpContext);
    };

    // Login is limited per IP address across all accounts (TEN-12); per-account protection is the lockout in LoginService.
    limiter.AddPolicy(LoginEndpoints.RateLimitPolicy, httpContext =>
    {
        var settings = httpContext.RequestServices.GetRequiredService<IOptions<LoginProtectionOptions>>().Value;

        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.IpLimit,
                Window = TimeSpan.FromMinutes(settings.IpWindowMinutes),
                QueueLimit = 0,
            });
    });

    // Asking for and using password reset links is limited per IP address.
    limiter.AddPolicy(PasswordResetEndpoints.RateLimitPolicy, httpContext =>
    {
        var settings = httpContext.RequestServices.GetRequiredService<IOptions<PasswordResetOptions>>().Value;

        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.IpLimit,
                Window = TimeSpan.FromMinutes(settings.IpWindowMinutes),
                QueueLimit = 0,
            });
    });

    limiter.AddPolicy(EmailVerificationEndpoints.ResendRateLimitPolicy, httpContext =>
    {
        // Read per request, so configuration added after startup (test overrides) still applies.
        var settings = httpContext.RequestServices.GetRequiredService<IOptions<EmailVerificationOptions>>().Value;

        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.ResendIpLimit,
                Window = TimeSpan.FromMinutes(settings.ResendIpWindowMinutes),
                QueueLimit = 0,
            });
    });
});

builder.Services.AddScoped<SignupService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<RefreshService>();
builder.Services.AddScoped<LogoutService>();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddScoped<CreateTenantService>();

var app = builder.Build();

// Create the signing key now so a missing key stops the deployment instead of failing the first login.
app.Services.GetRequiredService<SigningKeyProvider>();

// Likewise, an unsupported or incomplete email provider setting stops the deployment instead of failing the first email.
app.Services.GetRequiredService<IEmailSender>();

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "AuthService");
app.MapSignup();
app.MapEmailVerification();
app.MapLogin();
app.MapRefresh();
app.MapLogout();
app.MapCurrentUser();
app.MapCreateTenant();
app.MapPasswordReset();

app.Run();

// Exposed so integration tests (TST-02) can boot the API in-process via WebApplicationFactory<Program>.
public partial class Program;
