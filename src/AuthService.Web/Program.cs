using AuthService.Web.Api;
using AuthService.Web.Components;
using AuthService.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Where the Auth Service API is. A missing or malformed address stops the app starting rather than failing the first page.
builder.Services.AddOptions<AuthApiOptions>()
    .Bind(builder.Configuration.GetSection(AuthApiOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AuthApiOptions>, AuthApiOptionsValidator>();

// Behind a proxy that ends HTTPS (the deployment in docs/deployment.md), the proxies listed here are believed about the visitor's address
// and the scheme. A typo stops the app starting rather than trusting the wrong thing.
builder.Services.AddOptions<ProxyOptions>().Bind(builder.Configuration.GetSection(ProxyOptions.SectionName));

// The cookie that holds the signed-in session is protected with keys. Kept in a folder (a volume in the container), they survive a restart,
// so a deploy does not make every cookie unreadable. Without a folder they live in memory only, which is right for development.
if (builder.Configuration["AuthService:DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    builder.Services.AddDataProtection()
        .SetApplicationName("AuthService.Web")
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<ClientAddressHandler>();

builder.Services.AddHttpClient<AuthApiClient>((provider, client) =>
    {
        var options = provider.GetRequiredService<IOptions<AuthApiOptions>>().Value;
        client.BaseAddress = options.BaseUrl;
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    })

    // Every call the web app makes for a visitor names the visitor's address, so the API's per-address limits and audit entries see them.
    .AddHttpMessageHandler<ClientAddressHandler>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddSessionAuthentication(builder.Environment);

var app = builder.Build();

// Checked here, before the host starts, as well as by ValidateOnStart: reading the value runs the validators, so a missing or unusable API
// address stops the app with its own message at once. Left to the host's start alone, a test host that is torn down as start fails can
// report a disposed service provider instead of the reason (AUTH-285).
_ = app.Services.GetRequiredService<IOptions<AuthApiOptions>>().Value;

var proxy =app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;
if (proxy.Problems().ToList() is { Count: > 0 } proxyProblems)
{
    throw new InvalidOperationException(string.Join(" ", proxyProblems));
}

// First, so everything after it (the secure cookie, the address sent on to the API) sees the visitor and not the proxy.
if (proxy.IsConfigured)
{
    app.UseForwardedHeaders(proxy.ToForwardedHeadersOptions());
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseSecurityHeaders();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// For a platform health check. It says only that the web app is running, never anything about the API or its data.
app.MapSessionEndpoints();
app.MapConsoleEndpoints();
app.MapPlatformEndpoints();
app.MapProductEndpoints();

app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

// The root of the platform console goes to its first part. It shows nothing itself: that part decides, with the API, whether the person may see it.
app.MapGet("/platform", () => Results.Redirect("/platform/audit")).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Lets the test project start the app.</summary>
public partial class Program;
