# AuthService.Authorization

The package a product API installs to enforce Auth Service tokens. A token is checked **locally**, with no call to the Auth Service per
request, against the public keys the Auth Service publishes.

Give it two things: where the Auth Service is, and the client secret the platform admin gave your product. The package learns the rest
(who issues tokens, your product's own audience, where the keys are) from the Auth Service.

```csharp
builder.Services.AddAuthServiceProduct(options => builder.Configuration.GetSection("AuthService").Bind(options));
// AuthService__Url=https://auth.example.com
// AuthService__ClientSecret=cl_….the-secret      (keep it in the host's secret store)

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/campaigns", (ClaimsPrincipal user) => ListFor(user.RequireTenantId()))
   .RequirePermission("campaign.read", AccessLevel.ReadOnly);
app.MapPost("/campaigns", CreateCampaign)
   .RequirePermission("campaign.create", AccessLevel.CreateRead);
```

`AddAuthServiceProduct` also registers `IAuthServiceClient`, for the calls a product makes as itself: it asks for the service token when it
needs one, keeps it, and asks again when it is about to expire.

```csharp
app.MapPost("/signup", async (IAuthServiceClient authService, Guid tenantId) =>
{
    var subscription = await authService.SubscribeTenantAsync(tenantId, maxSeats: 5);   // Active; subscribing again changes nothing
    return Results.Ok(subscription);
});
var product = await authService.GetProductAsync();     // your audience, whether you are available, your permission keys
```

A refusal is an `AuthServiceException` with the status and the Auth Service's own `Code`, such as `tenant_not_found`. It never carries the
secret or a token.

From 1.2.0 (on nuget.org, 1.2.1), `SendAsync(method, path, content)` calls any other Auth Service endpoint with the same service token (renewed once if refused);
the caller reads the response. `AuthService.Entitlements` builds on it (decision 0016).

### Setting it up by hand

If you would rather not have the package read anything from the Auth Service when it starts, say the three things yourself (the product's
own audience, the issuer and where the keys are). The checks are exactly the same.

```csharp
builder.Services.AddAuthServiceAuthorization(options =>
{
    options.Issuer = "AuthService";                                         // the Auth Service's issuer
    options.Audience = "my-product-api";                                    // YOUR product's own audience, as registered
    options.JwksUrl = new Uri("https://auth.example.com/.well-known/jwks.json");
});
```

Use one of the two, not both.

### If the Auth Service can't be reached

With `AddAuthServiceProduct`, the issuer and audience are read when the first token arrives and then kept for `ConfigurationCacheMinutes`
(60). If the Auth Service can't be reached at that moment, every token gets a plain **401** (never an error page) and the package tries again
after `MinimumRefreshSeconds` (30), so a stream of requests is not a stream of calls to the Auth Service. Once it has read them, a later
outage changes nothing: the last known settings and keys stay in use. A wrong client secret looks the same from outside (401), and the
package logs a warning saying only the kind of failure, never the secret.

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

## Installing

The package is on [nuget.org](https://www.nuget.org/packages/AuthService.Authorization) under the MIT licence. No extra feed or token is
needed:

```bash
dotnet add package AuthService.Authorization
```

The same versions are also pushed to the Auth Service's own GitHub Packages feed (a personal access token with `read:packages` is needed
there); use that only if you work inside the Auth Service organisation.

`samples/SampleProduct` is a small product API that installs the package this way and enforces tokens with it; its tests
(`samples/SampleProduct.Tests`) are the ones a product team would write.

## Releasing (for the Auth Service team)

A release is made by CI, never from a developer machine, and only from a commit that is on `main`:

1. Decide the version. The claim contract (`sub`, `tid`, `aud`, `permissions`) and the public API are the surface: a breaking change to
   either is a **new major version**, a backwards-compatible addition a new minor, a fix a patch. The Auth Service keeps issuing the
   old contract until products have moved.
2. Push a tag named `authorization-v<version>` on the merged commit, for example `authorization-v1.3.0` (or `authorization-v2.0.0-rc.1`
   for a pre-release).
3. The "Publish AuthService.Authorization" workflow checks the tag is a semantic version on `main`, runs the whole test suite, packs the
   package at the tag's version, pushes it to the GitHub Packages feed, restores it from there into the sample product and runs the
   sample's tests, and only then pushes the same package to nuget.org. A failure at any step publishes nothing further.
4. A published version is never replaced. If one is wrong, publish the next one. nuget.org cannot delete a version, only unlist it.

## Versioning

The package follows semantic versioning. The claim contract (`sub`, `tid`, `aud`, `permissions` as `key:Level`) and the public API are
its surface: a breaking change to either is a new major version, and the Auth Service keeps issuing the old contract until products
have moved.
