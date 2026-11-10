using System.Collections.Concurrent;
using System.Security.Claims;
using AuthService.Authorization;
using AuthService.Entitlements;

var builder = WebApplication.CreateBuilder(args);

// The three things a product tells the package: who issues tokens, which audience is its own (as registered with the Auth Service) and
// where the Auth Service publishes its keys. Nothing else about the Auth Service is needed, and it is never called per request.
builder.Services.AddAuthServiceAuthorization(options => builder.Configuration.GetSection("AuthService").Bind(options));

// Each organisation's plan and limits (AuthService.Entitlements): the Auth Service posts them to /auth-webhooks, signed with the webhook
// secret, and the package keeps the newest version. A real product also calls AddAuthServiceProduct, whose client the package uses to read
// an organisation it has not heard about yet; this sample starts without an Auth Service, so it relies on the webhooks.
builder.Services.AddAuthServiceEntitlements(options => builder.Configuration.GetSection("AuthService:Entitlements").Bind(options));

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

// A limit check: importing contacts may not take the organisation past its "contacts.reachable" limit. The limit is the organisation's
// own (plan, override or default) and is answered from what the webhooks delivered, with no call to the Auth Service.
var contacts = new ConcurrentDictionary<Guid, long>();
app.MapPost("/contacts/import", async (ClaimsPrincipal user, ImportContacts request, IEntitlements entitlements, CancellationToken cancellationToken) =>
    {
        var tenantId = user.RequireTenantId();
        var limit = await entitlements.GetLimitAsync(tenantId, "contacts.reachable", cancellationToken);
        var current = contacts.GetValueOrDefault(tenantId);
        if (request.Count < 1 || !limit.IsWithin(current + request.Count))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Contact limit reached",
                detail: $"Your plan allows {limit} reachable contacts and you have {current}. Ask your administrator about a bigger plan.",
                extensions: new Dictionary<string, object?> { ["code"] = "limit_reached", ["remaining"] = limit.Remaining(current) });
        }

        var total = contacts.AddOrUpdate(tenantId, request.Count, (_, existing) => existing + request.Count);
        return Results.Ok(new { tenantId, contacts = total, remaining = limit.Remaining(total) });
    })
    .RequirePermission("campaign.create", AccessLevel.CreateRead);

// Where the Auth Service sends its events. Register this address as the product's webhook callback. It checks the signature itself.
app.MapAuthServiceWebhook("/auth-webhooks");

app.Run();

/// <summary>The body of <c>POST /contacts/import</c>.</summary>
internal sealed record ImportContacts(long Count);

// Exposed so the tests can start the app in-process.
public partial class Program;
