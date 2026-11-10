# AuthService.Entitlements.Testing

Test helpers for products that use [AuthService.Entitlements](https://www.nuget.org/packages/AuthService.Entitlements): no Auth Service needed.

- **`FakeAuthService`** is an `IAuthServiceClient`. Set up limits, plans and organisations (`AddLimit`, `AddPlan`, `SetTenant`), register it
  in place of the real client, and the entitlement reads answer from it. Each `SetTenant` raises the organisation's version and change
  number, like the real service.
- **`SignedWebhook.Create(path, body, secret)`** builds a `POST` signed exactly like the Auth Service's (`X-Auth-Timestamp`,
  `X-Auth-Signature: v1=…`, `X-Auth-Event-Id`, `X-Auth-Event-Type`). The fake writes the bodies: `EntitlementsChangedEvent`,
  `ApplicationSubscribedEvent`, `ApplicationUnsubscribedEvent`.

```csharp
var auth = new FakeAuthService();
auth.AddLimit("contacts.reachable", "Reachable contacts", defaultValue: 1_000, unit: "contacts");
auth.SetTenant(tenantId, new Dictionary<string, long?> { ["contacts.reachable"] = 50_000 }, planKey: "growth", planName: "Growth");

await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.ConfigureTestServices(services =>
    services.AddSingleton<IAuthServiceClient>(auth)));
using var client = app.CreateClient();

var response = await client.SendAsync(SignedWebhook.Create("/auth-webhooks", auth.EntitlementsChangedEvent(tenantId), "whsec_test"));
```
