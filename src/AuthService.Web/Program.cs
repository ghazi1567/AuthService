using AuthService.Web.Api;
using AuthService.Web.Components;
using AuthService.Web.Security;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Where the Auth Service API is. A missing or malformed address stops the app starting rather than failing the first page.
builder.Services.AddOptions<AuthApiOptions>()
    .Bind(builder.Configuration.GetSection(AuthApiOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AuthApiOptions>, AuthApiOptionsValidator>();

builder.Services.AddHttpClient<AuthApiClient>((provider, client) =>
    {
        var options = provider.GetRequiredService<IOptions<AuthApiOptions>>().Value;
        client.BaseAddress = options.BaseUrl;
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddSessionAuthentication(builder.Environment);

var app = builder.Build();

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

app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

// The root of the platform console goes to its first part. It shows nothing itself: that part decides, with the API, whether the person may see it.
app.MapGet("/platform", () => Results.Redirect("/platform/audit")).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Lets the test project start the app.</summary>
public partial class Program;
