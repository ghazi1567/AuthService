using System.Security.Claims;
using AuthService.Authorization;

var builder = WebApplication.CreateBuilder(args);

// The three things a product tells the package: who issues tokens, which audience is its own (as registered with the Auth Service) and
// where the Auth Service publishes its keys. Nothing else about the Auth Service is needed, and it is never called per request.
builder.Services.AddAuthServiceAuthorization(options => builder.Configuration.GetSection("AuthService").Bind(options));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// The tenant is only ever the token's. Never take it from the path, the query string, a header or the body.
app.MapGet("/campaigns", (ClaimsPrincipal user) => Results.Ok(new { tenantId = user.RequireTenantId(), campaigns = Array.Empty<string>() }))
    .RequirePermission("campaign.read", AccessLevel.ReadOnly);
app.MapPost("/campaigns", (ClaimsPrincipal user) => Results.Ok(new { tenantId = user.RequireTenantId(), created = true }))
    .RequirePermission("campaign.create", AccessLevel.CreateRead);
app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { tenantId = user.RequireTenantId(), userId = user.GetUserId() }))
    .RequireTenant();
app.MapGet("/health", () => Results.Ok("ok")).AllowAnonymous();

app.Run();

// Exposed so the tests can start the app in-process.
public partial class Program;
