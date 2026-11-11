# AuthService.Entitlements

Keeps an Auth Service product up to date with each organisation's **plan and limits**, and answers "what is this organisation's limit?"
without calling the Auth Service on every request.

- **Webhooks:** `MapAuthServiceWebhook("/auth-webhooks")` checks the Auth Service's signature (HMAC-SHA256 over `{timestamp}.{body}`, any of
  the `v1=` signatures during a secret rotation, five-minute window), reads the event (`ApplicationSubscribed`, `ApplicationUnsubscribed`,
  `EntitlementsChanged`), drops a second delivery of the same event, keeps only a **newer version**, and answers `204` straight away.
- **A store:** `IEntitlementStore` keeps each organisation's newest snapshot. The default is in memory; register your own (your database)
  for several instances or to survive a restart.
- **Reads:** `IEntitlementsClient` reads the product's limits and plans, one organisation, and every organisation in change order
  (`GetChangesAsync`, `CatchUpAsync`) for a first sync or after missed events. It uses the `IAuthServiceClient` from
  `AuthService.Authorization` (the same cached service token), so there is no second client to configure.
- **A limit check:** `IEntitlements.GetLimitAsync(tenantId, key)` answers from the store and reads an organisation once if the store has
  none. A limit missing from a snapshot (added later) is the limit's **default, never unlimited**. `LimitValue` has `IsUnlimited`,
  `IsWithin(current)`, `Remaining(current)` and `IsOn` (for `flag` limits).

## Install

```csharp
builder.Services.AddAuthServiceProduct(options => builder.Configuration.GetSection("AuthService").Bind(options));
builder.Services.AddAuthServiceEntitlements(options => builder.Configuration.GetSection("AuthService:Entitlements").Bind(options));

var app = builder.Build();
app.MapAuthServiceWebhook("/auth-webhooks");   // register https://your-product/auth-webhooks as the product's webhook callback
```

Settings (environment variables shown):

| Setting | What it is |
|---|---|
| `AuthService__Entitlements__WebhookSecrets__0` | The webhook signing secret, shown once when the callback was registered. During a rotation add the new one as `__1`. Required. |
| `AuthService__Entitlements__SignatureTolerance` | How far a webhook's signed time may be from now. Default `00:05:00`. |
| `AuthService__Entitlements__LimitCacheDuration` | How long limit definitions (for defaults) are kept. Default `00:10:00`. |

## Use

```csharp
app.MapPost("/contacts/import", async (ClaimsPrincipal user, ImportContacts request, IEntitlements entitlements) =>
{
    var tenantId = user.RequireTenantId();
    var limit = await entitlements.GetLimitAsync(tenantId, "contacts.reachable");
    if (!limit.IsWithin(currentContacts + request.Count))
    {
        return Results.Problem(statusCode: 403, title: "Contact limit reached");
    }
    // ...
});
```

**Catching up** after downtime, or on first start: keep the last change number you applied, and call

```csharp
lastChange = await entitlementsClient.CatchUpAsync(store, lastChange);
```

Only higher versions replace what the store has, so running it while webhooks arrive is safe.

**Reacting to an event** (a cancellation, say): set `options.OnEvent`. It runs after the store is updated; an exception answers `500`, so the
Auth Service sends the event again later.

## Rules the package follows

- `entitlements` is always the complete set, never a difference; `null` is unlimited.
- A version equal to or lower than the one kept is ignored. Events can arrive twice and out of order.
- New optional fields in an event are ignored. An event with a `schemaVersion` above 1 is answered `400` (update the package), and the Auth
  Service keeps retrying it, so it shows as failed in the platform console.
- The tenant is always the token's (`RequireTenantId()` from `AuthService.Authorization`), never something from the request.

## Testing

Install `AuthService.Entitlements.Testing`: `FakeAuthService` answers the reads from what a test sets up, and `SignedWebhook.Create` posts
events signed like the real ones.

## Releases

Released together with the `AuthService.Authorization` version it needs (1.2.1 or later on nuget.org, for `IAuthServiceClient.SendAsync`). Tag
`entitlements-v<version>` on main; see `.github/workflows/publish-entitlements-package.yml`.
