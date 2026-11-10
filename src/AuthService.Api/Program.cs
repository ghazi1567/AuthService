using System.Globalization;
using System.Text.Json.Serialization;
using AuthService.Api.Common;
using AuthService.Api.Email;
using AuthService.Api.Email.Providers;
using AuthService.Api.Features.Admin;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.ApiKeys;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.CurrentUser;
using AuthService.Api.Features.Discovery;
using AuthService.Api.Features.EmailVerification;
using AuthService.Api.Features.Jwks;
using AuthService.Api.Features.Invitations;
using AuthService.Api.Features.Login;
using AuthService.Api.Features.Members;
using AuthService.Api.Features.Logout;
using AuthService.Api.Features.PasswordReset;
using AuthService.Api.Features.Refresh;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Webhooks;
using AuthService.Api.Features.TokenExchange;
using AuthService.Api.Features.Tenants;
using AuthService.Api.Observability;
using AuthService.Api.RateLimiting;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
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

// Reverse proxies whose X-Forwarded-For is believed, so rate limits and audit entries see the real client address.
builder.Services.AddOptions<ProxyOptions>()
    .Bind(builder.Configuration.GetSection(ProxyOptions.SectionName));

// Salted PBKDF2 hashes; the hash is only ever stored, never returned by an API.
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

// Enums are written by name ("Active"), not number.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<IdentityTokenOptions>(builder.Configuration.GetSection(IdentityTokenOptions.SectionName));

// Signing keys (TOK-02): stored encrypted in the database, rotated on a schedule, published at /.well-known/jwks.json.
builder.Services.AddSingleton<KeyEncryption>();
builder.Services.AddSingleton<SigningKeyService>();
builder.Services.AddScoped<SigningKeyRotation>();
builder.Services.AddHostedService<SigningKeyWorker>();
builder.Services.AddSingleton<IdentityTokenService>();

// Bearer authentication for identity tokens (TEN-06 onwards).
builder.Services.AddIdentityTokenAuthentication();

// Email (NTF-01): queued in the database, sent by a background worker through whichever provider is configured.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection(DiscoveryOptions.SectionName));
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

// Webhooks to products (SUB-06): signed, delivered from the outbox with retries.
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.SectionName));
builder.Services.AddWebhookHttpClient();
builder.Services.AddScoped<WebhookDeliveryService>();
builder.Services.AddHostedService<WebhookDispatcher>();

// Login protection (TEN-12).
builder.Services.Configure<LoginProtectionOptions>(builder.Configuration.GetSection(LoginProtectionOptions.SectionName));

// Audit log (AUD-01): entries are added in the same transaction as the action they record; old ones are deleted daily.
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<AuditOptions>(builder.Configuration.GetSection(AuditOptions.SectionName));
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<AuditRetention>();
builder.Services.AddHostedService<AuditRetentionWorker>();

// Password reset (TEN-10).
builder.Services.Configure<PasswordResetOptions>(builder.Configuration.GetSection(PasswordResetOptions.SectionName));
builder.Services.AddScoped<PasswordResetService>();

// Email verification (TEN-11).
builder.Services.Configure<EmailVerificationOptions>(builder.Configuration.GetSection(EmailVerificationOptions.SectionName));
builder.Services.AddScoped<EmailVerificationService>();

// Limits how often one IP address (or person) can use the endpoints that guess or send things (PLT-04). Counters are kept in the
// service's own database by default, so every instance shares them; Auth:RateLimit:Store = Memory keeps them per process (development,
// tests, a single instance). Behind a proxy, list the proxy in Auth:Proxy so this sees the client's address and not the proxy's.
builder.Services.AddOptions<RateLimitOptions>().Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.AddSingleton<IRateLimitStore>(provider => provider.GetRequiredService<IOptions<RateLimitOptions>>().Value.UsesPostgres
    ? new PostgresRateLimitStore(provider.GetRequiredService<IServiceScopeFactory>())
    : new InMemoryRateLimitStore());
builder.Services.AddHostedService<RateLimitCleanupWorker>();

builder.Services.AddScoped<SignupService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<RefreshService>();
builder.Services.AddScoped<LogoutService>();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddScoped<CreateTenantService>();
builder.Services.AddScoped<RegisterApplicationService>();
builder.Services.AddScoped<PermissionCatalogService>();
builder.Services.AddScoped<RoleTemplateService>();
builder.Services.Configure<ApplicationCredentialOptions>(builder.Configuration.GetSection(ApplicationCredentialOptions.SectionName));
builder.Services.AddScoped<ServiceTokenService>();
builder.Services.AddScoped<SecretRotationService>();
builder.Services.AddScoped<SubscribeTenantService>();
builder.Services.AddScoped<SeatLimitService>();
builder.Services.AddScoped<TenantStatusService>();
builder.Services.AddScoped<SubscriptionStatusService>();
builder.Services.AddScoped<PermissionResolver>();
builder.Services.Configure<TokenExchangeOptions>(builder.Configuration.GetSection(TokenExchangeOptions.SectionName));
builder.Services.AddScoped<TokenExchangeService>();
builder.Services.AddScoped<BuiltInApplicationSync>();
builder.Services.AddScoped<CreateRoleService>();
builder.Services.AddScoped<ManageRolesService>();
builder.Services.AddScoped<AssignRolesService>();
builder.Services.AddScoped<MemberAccessService>();
builder.Services.AddScoped<MembersService>();
builder.Services.Configure<InvitationOptions>(builder.Configuration.GetSection(InvitationOptions.SectionName));
builder.Services.AddScoped<InviteUserService>();
builder.Services.AddScoped<ManageInvitationsService>();
builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection(ApiKeyOptions.SectionName));
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<ApiKeyValidationService>();
builder.Services.AddScoped<AcceptInvitationService>();

// Health, readiness and metrics (PLT-05).
builder.Services.AddOptions<ObservabilityOptions>()
    .Bind(builder.Configuration.GetSection(ObservabilityOptions.SectionName));
builder.Services.AddSingleton<AuthMetrics>();
builder.Services.AddScoped<ReadinessService>();

var app = builder.Build();

// Load the signing keys now, creating the first one on a new database, so a missing key-encryption secret or an unreachable
// database stops the deployment instead of failing the first login.
await app.Services.GetRequiredService<SigningKeyService>().RefreshAsync(app.Lifetime.ApplicationStopping);

// The Auth Service's own product and its permission catalog are stored now, so tenant admins can always get a token for it.
await using (var startupScope = app.Services.CreateAsyncScope())
{
    await startupScope.ServiceProvider.GetRequiredService<BuiltInApplicationSync>().SyncAsync(app.Lifetime.ApplicationStopping);
}

// Likewise, an unsupported or incomplete email provider setting stops the deployment instead of failing the first email.
app.Services.GetRequiredService<IEmailSender>();

// First, so everything after it (rate limits, audit entries) sees the client's address rather than the proxy's.
var proxy = app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;
if (proxy.Problems().ToList() is { Count: > 0 } proxyProblems)
{
    throw new InvalidOperationException(string.Join(" ", proxyProblems));
}

if (proxy.IsConfigured)
{
    app.UseForwardedHeaders(proxy.ToForwardedHeadersOptions());
}

// A typo in the networks allowed to read the metrics stops the deployment instead of locking the monitoring out, or letting everyone in.
if (app.Services.GetRequiredService<IOptions<ObservabilityOptions>>().Value.Problems().ToList() is { Count: > 0 } observabilityProblems)
{
    throw new InvalidOperationException(string.Join(" ", observabilityProblems));
}

// A typo in where the rate-limit counters live stops the deployment instead of quietly using the wrong store.
if (app.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value.Problems().ToList() is { Count: > 0 } rateLimitProblems)
{
    throw new InvalidOperationException(string.Join(" ", rateLimitProblems));
}

// Outside the exception handler, so a request that ends in an error is counted with its 500.
app.UseMiddleware<RequestMetricsMiddleware>();
app.UseExceptionHandler();

// Authentication comes first so a limit can be per person (token exchange). A token is checked locally, with no database call, so
// this costs a rate-limited request almost nothing.
app.UseAuthentication();
app.UseMiddleware<SharedRateLimitMiddleware>();
app.UseAuthorization();

app.MapGet("/", () => "AuthService").AllowAnonymous();
app.MapObservability();
app.MapSignup();
app.MapEmailVerification();
app.MapLogin();
app.MapRefresh();
app.MapLogout();
app.MapCurrentUser();
app.MapCreateTenant();
app.MapPasswordReset();
app.MapJwks();
app.MapDiscovery();
app.MapServiceToken();
app.MapCurrentApplication();
app.MapWebhookConfig();
app.MapListApplications();
app.MapMyApplications();
app.MapSubscriptions();
app.MapTokenExchange();
app.MapTenantProfile();
app.MapListSubscriptions();
app.MapRoles();
app.MapTenantPermissionCatalog();
app.MapMemberAccess();
app.MapMembers();
app.MapInvitations();
app.MapApiKeys();
app.MapTenantAudit();

// Everything under /api/auth/admin is for platform admins only (APP-04); the policy is applied to the whole group.
var admin = app.MapAdminGroup();
admin.MapSigningKeyAdmin();
admin.MapAuditAdmin();
admin.MapApplicationsAdmin();
admin.MapTenantsAdmin();
admin.MapWebhooksAdmin();

app.Run();

// Exposed so integration tests (TST-02) can boot the API in-process via WebApplicationFactory<Program>.
public partial class Program;
