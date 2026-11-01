# AuthService.Authorization

The package a product API installs to enforce Auth Service tokens. A token is checked **locally**, with no call to the Auth Service per
request, against the public keys the Auth Service publishes.

```csharp
builder.Services.AddAuthServiceAuthorization(options =>
{
    options.Issuer = "AuthService";                                         // the Auth Service's issuer
    options.Audience = "whatsapp-saas-api";                                 // YOUR product's own audience, as registered
    options.JwksUrl = new Uri("https://auth.example.com/.well-known/jwks.json");
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/campaigns", (ClaimsPrincipal user) => ListFor(user.RequireTenantId()))
   .RequirePermission("campaign.read", AccessLevel.ReadOnly);
app.MapPost("/campaigns", CreateCampaign)
   .RequirePermission("campaign.create", AccessLevel.CreateRead);
```

## What it enforces

- **Signature**, with the published keys, fetched once, refreshed every `KeyCacheMinutes` and when a token names an unknown key (at most
  every `MinimumRefreshSeconds`); the last good set is kept if a refresh fails. Only **ES256** is accepted.
- **Audience**: the token's `aud` must be this product's own, even when the signature is valid, so a token issued for another product
  cannot be replayed here. An identity token or a service token has no matching audience and is refused too.
- **Issuer and lifetime** (with a small clock skew). A tampered, expired, unsigned or malformed token is a clean **401**
  (`application/problem+json`, no exception text); a valid token that lacks the permission is **403**.
- **Permissions by level**, never by role name. The token's `permissions` claims are `key:Level`, with levels `ReadOnly`, `CreateRead`
  and `Full`. `RequirePermission("campaign.create", AccessLevel.CreateRead)` is met by `CreateRead` or `Full`.
- **The tenant** is `User.RequireTenantId()` (the token's `tid`). Take the tenant from nothing else: not the body, the path, the query
  string or a header.

## Finding endpoints that forgot a policy

Add a test that builds your application and fails if any endpoint is left open:

```csharp
var open = await EndpointPolicyAudit.FindEndpointsWithoutPermissionPolicyAsync(
    app.Services, isExempt: endpoint => endpoint.RoutePattern.RawText == "/health");
open.ShouldBeEmpty();
```

## Versioning

The package follows semantic versioning. The claim contract (`sub`, `tid`, `aud`, `permissions` as `key:Level`) and the public API are
its surface: a breaking change to either is a new major version, and the Auth Service keeps issuing the old contract until products
have moved.
