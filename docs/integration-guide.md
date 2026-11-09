# Connecting your app to the Auth Service

For the team that owns an app (a "product") and wants its people to sign in through the Auth Service, with each person's organisation
(tenant) and permissions arriving in a signed token. Written from the code and the reference in [`docs/reference/`](reference/README.md);
where this guide and the reference differ, the reference and the code win.

`<auth-host>` below is the address the Auth Service API is served on (`AUTH_HOST` in [deployment.md](deployment.md)). Ask the
platform admin for the real dev, staging and production values.

## Contents

1. [How it fits together](#1-how-it-fits-together)
2. [Step 1: get your app registered](#2-step-1-get-your-app-registered)
3. [Step 2: choose how people sign in](#3-step-2-choose-how-people-sign-in)
4. [Step 3: check every request](#4-step-3-check-every-request)
5. [Step 4: use permissions](#5-step-4-use-permissions)
6. [Step 5: bring tenants on and react to changes](#6-step-5-bring-tenants-on-and-react-to-changes)
7. [Machine callers: API keys](#7-machine-callers-api-keys)
8. [Lifetimes and what goes stale](#8-lifetimes-and-what-goes-stale)
9. [Errors you will meet](#9-errors-you-will-meet)
10. [What the Auth Service does not do](#10-what-the-auth-service-does-not-do)
11. [Go-live checklist](#11-go-live-checklist)

---

## 1. How it fits together

```text
 Person ──login──▶ Auth Service API ──identity token──▶ (sign-in screen or console)
                                                          │  choose organisation + your app
                                                          ▼
                                        POST /api/auth/token/exchange
                                                          │
                                                   PRODUCT TOKEN  (tid, sub, aud, permissions)
                                                          ▼
 Your app ◀── the token (form POST from the console, or from your own backend) ──
    │  validates it locally: signature (JWKS), issuer, audience, expiry   ← no call to the Auth Service per request
    ▼
 enforces permissions by level, takes the tenant from the token only

 Your backend ──client id + secret──▶ POST /api/auth/token ──service token──▶ subscribe tenants, set seats,
                                                                              register webhook, check API keys
 Auth Service ──signed webhook──▶ your callback   (ApplicationSubscribed / ApplicationUnsubscribed)
```

Three kinds of token, and which one you will see (all JWTs, ES256, signed by the same keys):

| | Identity token | **Product token** | Service token |
|---|---|---|---|
| Says | who a person is | who, in which one organisation, for **your** app, with which permissions | which app is calling the Auth Service |
| Lifetime | 15 min | 15 min | 10 min |
| You receive it | only if your backend signs people in itself | **yes, this is what your app checks** | you ask for it |
| `aud` | none | your app's audience | `auth-service` |

A product token carries: `iss`, `sub` (the person's user id, a GUID), `tid` (the organisation's id, a GUID), `aud`, `jti`, `iat`, `nbf`,
`exp`, and `permissions`, an array of `key:Level` strings such as `campaign.create:CreateRead`. It carries no email, no organisation name
and no plan or limit.

There is **no OIDC**: no `/authorize` endpoint, no discovery document, no client registration screen. Sign-in is the Auth Service's own
login API followed by a token exchange. Do not point an OIDC library at it.

## 2. Step 1: get your app registered

A **platform admin** (a user whose `is_platform_admin` flag was set in the database) does this once per app and per environment. Hand
them the values on the left; they hand back what is on the right.

| You provide | Platform admin does | You get back |
|---|---|---|
| `key` (lowercase, digits, hyphens, at most 63), display `name` | `POST /api/auth/admin/applications` with `key`, `name`, `jwtAudience` | `id`, `clientId`, **`clientSecret` (shown once)** |
| `jwtAudience`, for example `marketing-api` (letters, digits, `. _ : / -`; unique; not `auth-service`) | (same call) | |
| The permission catalogue (below) | `POST /api/auth/admin/applications/{id}/permissions` | the stored catalogue |
| Your launch URL, https (only if you use the console hand-off) | `PUT /api/auth/admin/applications/{id}/launch-url` with `{"url": "..."}` | |
| | `PUT /api/auth/admin/applications/{id}/active` with `{"isActive": true}` (needs at least one permission) | the app is now offered to tenants |

Notes:

- The **audience is permanent**: there is no route to edit it, and tokens already issued for a wrong value stop matching. Choose it
  carefully.
- The **client secret** is shown once and only its hash is stored. Keep it in your secret store, never in the front end. A lost secret
  is replaced by rotation (`POST .../secret/rotate`, platform admin), and the old one keeps working for a grace period (24 hours by
  default) so you can switch without downtime.
- A new app is **inactive** until activated. It can already get a service token while inactive.

### The permission catalogue

List the things your app controls, with a label, for example:

```json
{ "permissions": [
  { "key": "campaign.read",   "label": "View campaigns",   "description": "See campaigns and their results" },
  { "key": "campaign.create", "label": "Create campaigns" },
  { "key": "contacts.manage", "label": "Manage contacts" }
] }
```

- Keys are **lowercased** on the way in (`Campaign.Create` is stored as `campaign.create`); 1 to 100 characters, letters, digits, `.`,
  `_`, `-`. Use exactly the stored key in your code.
- Registering again is an **upsert**: new keys are added, changed labels update in place, a key you stop sending **stays** (roles may
  hold it). Keys can never be deleted. Resend the description on every call, or it is cleared.
- Tenant admins build their own **custom roles** from this catalogue in the Auth Service console. Each grant has a level:
  `ReadOnly` < `CreateRead` < `Full`. The tenant's **Owner** role automatically has every permission at `Full`, including ones added
  later.

## 3. Step 2: choose how people sign in

Pick **one** of two patterns. Both end with your app holding a product token.

> **A browser app cannot call the Auth Service API directly.** The API sends no CORS headers, so a React or other single-page app on its
> own origin cannot call `/api/auth/*` from the browser. Put a backend in front (a "backend for frontend", for example your `edge`
> service) or serve both under one origin through your reverse proxy. Every example below runs on your backend.

### Pattern A: the console hand-off (least work)

People sign in on the Auth Service console, choose their organisation and your app, and the console **form-posts the product token to
your launch URL**. You write one endpoint.

1. The platform admin has set your **launch URL** (https; `http` only for `localhost`).
2. The console reads which apps the person may open (`GET /api/auth/users/me/applications?tenantId=...`), runs the token exchange, and
   posts `application/x-www-form-urlencoded` with three fields to your launch URL: `access_token`, `token_type` (`Bearer`),
   `expires_in` (seconds).
3. Your launch endpoint validates the token (section 4), starts **your own** session (a cookie), and answers `303` to your app's home page.

```ts
// Node + Express + jose: the launch endpoint
import express from "express";
import { createRemoteJWKSet, jwtVerify } from "jose";

const jwks = createRemoteJWKSet(new URL(`https://${process.env.AUTH_HOST}/.well-known/jwks.json`), { cooldownDuration: 30_000 });
const app = express();

app.post("/auth/launch", express.urlencoded({ extended: false }), async (req, res) => {
  try {
    const { payload } = await jwtVerify(String(req.body.access_token), jwks, {
      algorithms: ["ES256"], issuer: "AuthService", audience: "marketing-api", clockTolerance: 30,
    });
    // start your own session: remember payload.sub, payload.tid, payload.permissions and payload.exp
    req.session = { userId: payload.sub, tenantId: payload.tid, permissions: payload.permissions, expiresAt: payload.exp };
    res.redirect(303, "/");                       // never put the token in a URL
  } catch {
    res.status(401).send("Sign-in failed");
  }
});
```

- The token is in a **form body, never a URL**, so it stays out of history, logs and referrers. Keep it that way: do not copy it into a
  redirect.
- The posted token is valid for 15 minutes and **cannot be refreshed by you** (you do not hold the person's refresh token). When your
  session outlives the token, send the person back to the console to open your app again.
- The person's session in the console and your session are separate. Ending one does not end the other.
- The `AuthService.Authorization` package reads the `Authorization` header only. In pattern A either validate the posted token with a
  JWT library as above, or place it in a header yourself and reuse the package.

### Pattern B: your own sign-in screen, your backend does the calls

You build the login form. Your backend talks to the Auth Service and keeps the tokens **server-side**.

```text
POST https://<auth-host>/api/auth/login          { "email": "...", "password": "..." }
 -> 200 { accessToken, tokenType, expiresIn, refreshToken, memberships: [{ tenantId, tenantName, status }] }

POST https://<auth-host>/api/auth/token/exchange  Authorization: Bearer <accessToken>
                                                   { "tenantId": "<guid>", "applicationId": "<your app's id>" }
 -> 200 { accessToken: "<PRODUCT TOKEN>", tokenType: "Bearer", expiresIn: 900 }

POST https://<auth-host>/api/auth/refresh         { "refreshToken": "..." }      (rotates: store the NEW refresh token)
 -> 200 { accessToken, tokenType, expiresIn, refreshToken }

POST https://<auth-host>/api/auth/logout          { "refreshToken": "..." }      -> 204
```

- `memberships` tells you which organisations the person belongs to. If there is more than one, let them choose; the exchange is per
  organisation. `GET /api/auth/users/me` (identity token) returns the profile and memberships again later.
- The exchange is refused (403 `exchange_denied`) unless the person is an Active member, **the organisation has an Active subscription
  to your app, and the person has a seat** (section 6). A suspended organisation gets `tenant_suspended`.
- Keep the identity and refresh tokens on your server (session store). Give the browser only your own session cookie, never an Auth
  Service token. When the product token is close to expiry, refresh the identity token and exchange again.
- Sign-up (`POST /api/auth/signup`), email verification and password reset are Auth Service features with their own screens in the
  console. If you build your own screens you call those endpoints too; the console's versions are the reference
  ([reference part 1](reference/01-accounts-and-sign-in.md)). A person who has no verified email cannot sign in.
- Login and token exchange are **rate limited** (the exchange allows 30 per person per minute). On `429` honour `Retry-After`.

## 4. Step 3: check every request

Validate the product token **locally** on every request. Never call the Auth Service per request, and never trust a token you did not
validate.

What to check, all required:

| Check | Value |
|---|---|
| Algorithm | **ES256 only** (refuse `none`, HMAC and anything else) |
| Signature | against the public keys at `GET https://<auth-host>/.well-known/jwks.json`, picked by the token's `kid` |
| `iss` | `AuthService` (the Auth Service's `Auth:Tokens:Issuer`; ask the admin if it was changed) |
| `aud` | **your app's registered audience**, exact match. An identity token (no `aud`) or a service token (`aud` = `auth-service`) must fail |
| `exp` / `nbf` | with at most 30 seconds of clock tolerance |
| Tenant | the `tid` claim, parsed as a non-empty GUID. **Take the tenant from the token and nowhere else**: not the body, path, query string or a header |

Key handling: cache the key set (about 10 minutes), and refetch when a token names an unknown `kid` (at most about every 30 seconds).
Keys rotate every 90 days with the next key published 24 hours before it signs, so a cache like this never breaks. If the Auth Service
is unreachable after your first successful fetch, keep using the last good keys.

### .NET: use the package

```bash
dotnet add package AuthService.Authorization
```

```csharp
builder.Services.AddAuthServiceAuthorization(o =>
{
    o.Issuer = "AuthService";
    o.Audience = "marketing-api";                                  // exactly as registered
    o.JwksUrl = new Uri("https://<auth-host>/.well-known/jwks.json");   // https, unless AllowInsecureJwksUrl in development
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/campaigns", (ClaimsPrincipal user) => List(user.RequireTenantId()))
   .RequirePermission("campaign.read", AccessLevel.ReadOnly);
app.MapPost("/campaigns", Create)
   .RequirePermission("campaign.create", AccessLevel.CreateRead);
```

The package gives you a clean `401` (`application/problem+json`, no exception text) for a bad token and `403` for a good token without
the permission. Add the endpoint audit to a test so a forgotten policy fails the build:

```csharp
var open = await EndpointPolicyAudit.FindEndpointsWithoutPermissionPolicyAsync(
    app.Services, isExempt: e => e.RoutePattern.RawText == "/health");
open.ShouldBeEmpty();
```

`samples/SampleProduct` and its tests are a working example. Package details: [`src/AuthService.Authorization/README.md`](../src/AuthService.Authorization/README.md).

### Any other stack: validate with a JWT library

Node with `jose`:

```ts
import { createRemoteJWKSet, jwtVerify } from "jose";

const jwks = createRemoteJWKSet(new URL("https://<auth-host>/.well-known/jwks.json"), { cooldownDuration: 30_000 });

export async function readToken(bearer: string) {
  const { payload } = await jwtVerify(bearer, jwks, {
    algorithms: ["ES256"], issuer: "AuthService", audience: "marketing-api", clockTolerance: 30,
  });
  return {
    userId: String(payload.sub),
    tenantId: String(payload.tid),                                   // GUID; reject if missing
    permissions: (payload.permissions as string[] | undefined) ?? [],
  };
}
```

Python (`PyJWT` with `cryptography`) and Go (`golang-jwt` with a JWKS cache) follow the same table: ES256, issuer, audience, expiry, `kid`
lookup. Do not use a library mode that accepts the algorithm named in the token header.

## 5. Step 4: use permissions

Permissions are checked **by level, never by role name**. A role name in a token grants nothing.

- Parse each `permissions` entry at the **last** `:`: `campaign.create:CreateRead` is key `campaign.create`, level `CreateRead`. The
  level name is case-sensitive; ignore entries you cannot parse. If a key appears twice, the highest level wins.
- Levels are ordered `None` < `ReadOnly` < `CreateRead` < `Full`; a higher level includes the lower ones. `CreateRead` or `Full` passes a
  `CreateRead` check.
- Enforce permissions **on the server** for every endpoint. Hiding a button is a convenience, not a control.

```ts
const order = { ReadOnly: 1, CreateRead: 2, Full: 3 } as const;
export function has(perms: string[], key: string, need: keyof typeof order) {
  return perms.some(p => {
    const i = p.lastIndexOf(":");
    const level = p.slice(i + 1) as keyof typeof order;
    return p.slice(0, i) === key && (order[level] ?? 0) >= order[need];
  });
}
```

A person with a seat but no roles gets a valid token with an **empty** `permissions` array: every protected endpoint answers 403.

## 6. Step 5: bring tenants on and react to changes

A person can only open your app if their organisation has an **Active subscription** to it **and** they hold a **seat**. Only your app
(with its service token) can create a subscription; the organisation's people cannot subscribe themselves.

### Get a service token

```text
POST https://<auth-host>/api/auth/token
Content-Type: application/x-www-form-urlencoded     (JSON also works)
grant_type=client_credentials&client_id=<cl_...>&client_secret=<secret>
 -> 200 { "access_token": "...", "token_type": "Bearer", "expires_in": 600 }
```

Cache it for a little less than 10 minutes and ask again when it expires. The endpoint is limited to 60 requests per IP address per
minute across all apps, so do not request one per call. `401 invalid_client` is one answer for an unknown client, a wrong secret and an
expired secret.

### Subscribe a tenant (your checkout or onboarding calls this)

```text
POST https://<auth-host>/api/auth/tenants/{tenantId}/subscriptions        Authorization: Bearer <service token>
{ "maxSeats": 25 }                                                        (omit for unlimited; at least 1)
 -> 201 on first subscribe, 200 if it was already Active or is re-activated
```

- Every Owner of the tenant gets a seat automatically. Other people get seats from a tenant admin in the console
  (`access.manage` permission), up to `maxSeats`.
- Change the limit: `PATCH /api/auth/tenants/{tenantId}/subscriptions/{yourAppId}/seats` with `{"maxSeats": n}` (or `null`).
- Suspend or cancel: `PATCH .../subscriptions/{yourAppId}` with `{"status": "Suspended"}` or `"Cancelled"`. Subscribing again re-activates
  and restores earlier seats. A cancelled subscription cannot be suspended.
- A tenant that does not exist is `404 tenant_not_found`; a suspended tenant is `409 tenant_suspended`; your app not yet active is
  `400 application_not_active`.
- Your own record: `GET /api/auth/applications/me` returns your `id`, `key`, `jwtAudience`, `clientId` and `isActive`.

### Register your webhook

```text
PUT https://<auth-host>/api/auth/applications/me/webhook                  Authorization: Bearer <service token>
{ "url": "https://app.example.com/hooks/auth" }
 -> 200 { "url": "...", "signingSecret": "whsec_...", "previousSecretExpiresAt": null }
```

The `signingSecret` is shown **once** (and again only when you rotate with `"rotateSecret": true`; `"overlapHours"` 0 to 720 keeps the old
secret valid meanwhile, default 24). The URL must be https and public. Events queued before you register wait until you do, for up to 24
hours.

### Receive events

Two events exist: `ApplicationSubscribed` and `ApplicationUnsubscribed`. Body fields: `eventId`, `eventType`, `occurredAt`, `tenantId`,
`applicationId`, plus `maxSeats` (subscribed; may be null) or `status` (`Suspended` or `Cancelled`; unsubscribed).

Headers: `X-Auth-Event-Id`, `X-Auth-Event-Type`, `X-Auth-Timestamp` (Unix seconds of this attempt), `X-Auth-Signature`
(`v1=<hex>`, or two comma-separated entries while a secret is being rotated).

```ts
import { createHmac, timingSafeEqual } from "node:crypto";

// Use the RAW body bytes (express.raw / req.text), never re-serialised JSON.
export function verify(rawBody: string, headers: Headers, secrets: string[]): boolean {
  const ts = headers.get("x-auth-timestamp") ?? "";
  if (Math.abs(Date.now() / 1000 - Number(ts)) > 300) return false;          // 5 minutes; each retry has its own timestamp
  const given = (headers.get("x-auth-signature") ?? "").split(",").map(s => s.trim());
  return secrets.some(secret => {
    const expected = "v1=" + createHmac("sha256", secret).update(`${ts}.${rawBody}`).digest("hex");   // key = the whole string, with whsec_
    return given.some(g => g.length === expected.length && timingSafeEqual(Buffer.from(g), Buffer.from(expected)));
  });
}
```

Then:

1. **Drop duplicates** by `X-Auth-Event-Id`: delivery is at-least-once, and a retry carries the same id.
2. **Do not rely on order.** A newer event can arrive before an older one. Compare `occurredAt` and, when it matters, the current state.
3. Answer **2xx within 10 seconds** with no redirect (a redirect counts as a failure). Do slow work afterwards.
4. Failed deliveries are retried at about 1 minute, 5 minutes, 30 minutes, 2, 6 and 12 hours, until 24 hours after the event, then marked
   failed. A platform admin can resend a failed event.

Events tell you a subscription started or ended. They do **not** carry plan or limit values beyond `maxSeats`.

## 7. Machine callers: API keys

For a tenant's own backend calling your app with no person signing in. A tenant admin creates a key in the Auth Service console, picks
your app and exact permissions, and sees the raw key **once** (`ak_<keyId>.<secret>`). It can be rotated with an overlap and revoked at
once. Your app checks a presented key with its service token:

```text
POST https://<auth-host>/api/auth/apikeys/validate                          Authorization: Bearer <service token>
{ "key": "ak_...", "permission": "campaign.read", "level": "ReadOnly" }     (permission and level are optional)
 -> 200 { tenantId, keyId, name, applicationId, permissions: [{ permission, level }] }
```

- Use the returned `tenantId` and nothing else for the tenant.
- `401 invalid_api_key` covers unknown, malformed, wrong secret, revoked, expired, suspended tenant and a key made for another app:
  treat it as no credential. `403 subscription_not_active` and `403 outside_key_scope` are real answers.
- The response is `no-store` and each call reads the database. If you cache results, a revoked key keeps working for that long, so keep
  the cache short.

## 8. Lifetimes and what goes stale

| Thing | Lifetime | What it means for you |
|---|---|---|
| Product token | 15 minutes | Nothing is revoked early. A removed role, revoked seat, suspension or disabled member is honoured by your app only after the token you hold expires (up to 15 minutes), unless your app checks sooner. |
| Identity token | 15 minutes | Renewed with the refresh token (pattern B). |
| Refresh token | 30 days, rotated on use | Store the newest one; an old one stops working. |
| Service token | 10 minutes | Ask again; do not log it. |
| Signing keys | 90 days, published 24 hours ahead | Cache and refetch on an unknown `kid`; nothing for you to do. |
| Webhook secret | until rotated | Rotate with an overlap so no event is lost. |
| Client secret | until rotated | Rotate through the platform admin with a grace period. |

If your app must cut access sooner than 15 minutes (for example on cancellation), react to the `ApplicationUnsubscribed` webhook and end
that tenant's sessions yourself.

## 9. Errors you will meet

| You see | Meaning |
|---|---|
| `401 unauthorized`, from your own checks | Bad, expired or foreign token, wrong issuer or audience, unreachable JWKS on first fetch, clock off by more than 30 seconds |
| `403 exchange_denied` on exchange | Not an Active member, unknown tenant or app, app inactive, no Active subscription, or no seat. One answer on purpose |
| `403 tenant_suspended` on exchange | The organisation is suspended |
| `403` with an empty body from the Auth Service | The wrong kind of token for that endpoint (for example a product token on an identity endpoint) |
| `400` validation problem | Field errors are named in the response |
| `409 no_seat_available` | The subscription's seat limit is reached |
| `429 rate_limited` | Honour `Retry-After` |
| `503 rate_limit_unavailable` | The Auth Service could not reach its own database; retry after the `Retry-After` header |

Debug a token by decoding its three parts (do not paste real tokens into third-party sites): `kid` and `alg` in the header, `iss`,
`aud`, `tid`, `exp` in the payload. "Every token is 401 on a fresh app" is almost always a wrong `Issuer`, a wrong `Audience`, or a
JWKS URL your server cannot reach.

## 10. What the Auth Service does not do

Be aware of these before you design around them:

- **No OIDC or social sign-in.** Only email and password through its own API.
- **No plans, plan names or limit values.** A subscription has a status and a seat limit (`maxSeats`) and nothing else. If your product
  has plans, you own them (for example by keeping the tenant's plan in your own database, keyed by `tenantId`).
- **No read of a tenant's subscription by your app.** Your service token can create and change subscriptions, but not list them. Keep your
  own record, fed by your calls and the two webhook events. (A tenant admin can read the subscription list with an admin token.)
- **No browser access to the API** (no CORS), see section 3.
- **No early revocation** of tokens, see section 8.
- **No way for your app to register its own permissions or edit its own audience.** The platform admin does the first; the second cannot
  be changed.
- **Creating a tenant is signing up.** There is no platform-admin route that creates a tenant or assigns a plan. Tenants come from
  `POST /api/auth/signup` or from an existing person creating another organisation. A new tenant cannot open your app until your
  backend subscribes it.

## 11. Go-live checklist

- [ ] App registered in the target environment; `clientId` and `clientSecret` in your secret store (not in the repo or the front end).
- [ ] Permission catalogue registered; app **activated**; launch URL set (pattern A).
- [ ] `Issuer`, `Audience` and `JwksUrl` set per environment; `JwksUrl` is https and reachable from your servers.
- [ ] Every endpoint checks the token and the permission; the tenant comes from `tid` only; a test asserts no endpoint is left open.
- [ ] A token for another app, an identity token, a service token, an expired token and a forged one are all refused (the sample
      product's tests show how).
- [ ] Service token cached and refreshed; client secret rotation rehearsed.
- [ ] Webhook registered; signature verified on the raw body; duplicates dropped; answers 2xx within 10 seconds.
- [ ] Tenant onboarding calls `POST .../subscriptions`; seats granted to the first people.
- [ ] Browser never holds an Auth Service token; refresh and identity tokens stay server-side.
- [ ] Sign-out behaviour decided (your session versus the console's) and the 15-minute staleness accepted or handled.
