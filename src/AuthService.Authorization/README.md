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

## Installing

The package is published to this repository's GitHub Packages feed. Add the feed once (a personal access token with `read:packages`, or the
`GITHUB_TOKEN` in a GitHub Actions workflow of the same organisation):

```bash
dotnet nuget add source https://nuget.pkg.github.com/<owner>/index.json --name authservice \
  --username <your-github-user> --password <token> --store-password-in-clear-text
dotnet add package AuthService.Authorization
```

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
   package at the tag's version, pushes it to the feed, and then restores it from the feed into the sample product and runs the sample's
   tests. A failure at any step publishes nothing further.
4. A published version is never replaced. If one is wrong, publish the next one.

## Versioning

The package follows semantic versioning. The claim contract (`sub`, `tid`, `aud`, `permissions` as `key:Level`) and the public API are
its surface: a breaking change to either is a new major version, and the Auth Service keeps issuing the old contract until products
have moved.
