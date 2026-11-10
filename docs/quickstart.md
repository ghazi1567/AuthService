# Quickstart: connect a product to the Auth Service

Two people do this: the **platform admin** (once, in the console) and the **product team** (a few lines of code). When it works, people
sign in on the console, choose their organisation and your product, and your API receives a signed token with their organisation and
their permissions. For everything behind these steps, see the [integration guide](integration-guide.md).

## What the product team needs from the admin

Two values, and nothing else:

| | Example | Notes |
|---|---|---|
| The Auth Service address | `https://auth.example.com` | The API's address (`AUTH_HOST`), not the console's |
| The client secret | `cl_AbC123….the-secret` | One string with the client id in it. It is shown once. Keep it in the host's secret store. |

## 1. The platform admin registers the product (about five minutes)

Sign in to the console as a platform admin and open **Products → Register a product**.

1. **Name** and **key** (a short permanent code such as `marketing`). Leave the **token audience** empty to get `<key>-api`; it cannot be
   changed later.
2. **Where it opens**: the address of your product's launch endpoint, `https://…/api/session/launch`. For development, plain
   `http://localhost:<port>/…` is accepted.
3. **Permissions**, and optionally the **roles** the product comes with, as the JSON the product's team keeps next to its code:

   ```json
   {
     "permissions": [
       { "key": "campaign.view", "label": "View campaigns", "description": "See campaigns and their results" },
       { "key": "campaign.send", "label": "Send campaigns" }
     ],
     "roles": [
       { "name": "Marketer", "description": "Builds and sends campaigns",
         "permissions": [ { "key": "campaign.view", "level": "ReadOnly" }, { "key": "campaign.send", "level": "CreateRead" } ] },
       { "name": "Analyst", "permissions": [ { "key": "campaign.view", "level": "ReadOnly" } ] }
     ]
   }
   ```

   Levels are `None`, `ReadOnly`, `CreateRead` and `Full`. Permissions can be added later but never removed.
4. Leave **Make it available now** ticked, and choose **Register product**.
5. Copy the **Settings for the product** box (it appears once, with the secret) and give it to the product team.

Then bring an organisation onto the product, so there is someone to sign in as:

- **Test organisation** on the product's row creates an organisation owned by a person who already has a verified account (you can use
  your own), subscribes it, and gives it the product's roles. The owner takes the first seat.
- **Subscribe organisation** brings an existing organisation on by its id.

## 2. The product team connects (about ten minutes)

```bash
dotnet add package AuthService.Authorization     # 1.1.0 or later
```

```csharp
builder.Services.AddAuthServiceProduct(options => builder.Configuration.GetSection("AuthService").Bind(options));
// AuthService__Url=https://auth.example.com
// AuthService__ClientSecret=cl_….the-secret

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/campaigns", (ClaimsPrincipal user) => ListFor(user.RequireTenantId()))
   .RequirePermission("campaign.view", AccessLevel.ReadOnly);
app.MapPost("/campaigns/send", Send)
   .RequirePermission("campaign.send", AccessLevel.CreateRead);
```

That is the whole setup. The package learns who issues tokens, your product's audience and where the keys are from the Auth Service, and
checks every token locally, with no call to the Auth Service per request. A bad token is a clean `401`; a good token without the
permission is a `403`. The tenant is `User.RequireTenantId()`, from the token only.

Not on .NET? Check the token with any JWT library (ES256, the issuer, your own audience) as in
[the integration guide](integration-guide.md#4-step-3-check-every-request).

## 3. Receive people when they sign in

When someone chooses your product in the console, the console form-posts their token to your launch address. Write one endpoint that
validates it, starts your own session and redirects. [The guide shows it](integration-guide.md#pattern-a-the-console-hand-off-least-work).
A browser app cannot call the Auth Service API directly (it sends no CORS headers), so this endpoint, like everything else that touches the
Auth Service, runs on your backend.

## 4. Check it works

- Sign in to the console as the owner of the test organisation, choose your product, and your launch endpoint receives a token.
- Your API answers `200` for that token, `401` for no token, and `403` for a person whose role lacks the permission.
- To subscribe organisations from your own sign-up, inject `IAuthServiceClient` and call `SubscribeTenantAsync`.

## 5. Know each organisation's plan and limits (if you use plans)

1. The platform admin adds your limits and plans in the console (**Products → Limits**, **Products → Plans**) and puts organisations on plans.
2. Register your webhook address with your service token (`PUT /api/auth/applications/me/webhook`) and keep the signing secret it shows once.
3. Install `AuthService.Entitlements`, call `AddAuthServiceEntitlements` with the secret (`AuthService__Entitlements__WebhookSecrets__0`) and
   `app.MapAuthServiceWebhook("/auth-webhooks")`.
4. Check limits with `IEntitlements.GetLimitAsync(tenantId, "contacts.reachable")`; on start, call `IEntitlementsClient.CatchUpAsync` with the
   last change number you kept. The [package README](../src/AuthService.Entitlements/README.md) has the details.

## Before going live

- Replace any `http://localhost` launch address with the real `https` one (**Products → Opens at**).
- Register the product in the production console, and keep its client secret in the production secret store.
- Rehearse replacing the secret (**Replace secret**): the old one keeps working for a while, so nothing stops.
- Read the [go-live checklist](integration-guide.md#11-go-live-checklist).
