# Tokens, signing keys, API keys and the authorization package

> Part 4 of the Auth Service reference. Overview, glossary and symptom index: [README](README.md).

This part explains how the Auth Service proves who someone is and what they may do, and how a separate product checks that proof
without calling the Auth Service on every request. The web side of opening a product is in [06-web-console.md](06-web-console.md).

## Contents

1. [The three kinds of token](#the-three-kinds-of-token)
2. [Token exchange](#token-exchange)
3. [Signing keys and the JWKS](#signing-keys-and-the-jwks)
4. [API keys](#api-keys)
5. [The authorization package and the sample product](#the-authorization-package-and-the-sample-product)
6. [The entitlements package](#the-entitlements-package)
7. [The hand-off into a product](#the-hand-off-into-a-product)
8. [Tenant isolation and the always-run isolation tests](#tenant-isolation-and-the-always-run-isolation-tests)
9. [Symptom to where to look (this part)](#symptom-to-where-to-look-this-part)
10. [Highest-risk areas in this part](#highest-risk-areas-in-this-part)
11. [Unknowns and assumptions to confirm](#unknowns-and-assumptions-to-confirm)

---

## The three kinds of token

### What it does

The Auth Service issues three kinds of signed token. They look alike (all are JWTs signed with ES256 by the same key) but each is
useful in one place only. Keeping them apart is what stops a token meant for one purpose being used for another.

- An **identity token** says who a person is and nothing else. It is what login and refresh return. It is used against the Auth
  Service's own "who am I" style endpoints and to start a token exchange.
- A **product token** says who the person is, which one organisation (tenant) they are working in, which one product it is for, and
  what they may do in that product. It is made by token exchange. A product accepts it; nothing else does.
- A **service token** says "this is product X calling the Auth Service". It is made from the product's client id and secret. It is
  used for the few endpoints a product calls about its own data (for example checking an API key).

Rules that hold for all three:

- Algorithm ES256 only. Issuer is `Auth:Tokens:Issuer` (default `AuthService`). A `kid` header names the signing key.
- Lifetimes (defined in `IdentityTokenOptions`, `src/AuthService.Api/Tokens/IdentityTokenOptions.cs`): identity 15 minutes
  (`AccessTokenMinutes`), product 15 minutes (`ProductTokenMinutes`), service 10 minutes (`ServiceTokenMinutes`).
- Nothing is revoked early. A token already issued stays valid until it expires, wherever it is checked locally. The short life is the
  bound on how stale a permission can get (decision 0005, "Consequences").
- A refresh token (30 days, `RefreshTokenDays`) is a different thing: an opaque secret stored hashed, not a JWT. It only gets a new
  identity token. It is covered in the login part.

| | Identity token | Product token | Service token |
|---|---|---|---|
| Made by | login and refresh; `IdentityTokenService.CreateAccessToken` | `POST /api/auth/token/exchange`; `IdentityTokenService.CreateProductToken` | `POST /api/auth/token` (OAuth client credentials); `IdentityTokenService.CreateServiceToken` |
| For | a person, in no tenant | a person, one tenant, one product | a product's own machine calls |
| `sub` | user id (a GUID) | user id (a GUID) | `app:<applicationId>`, never a GUID, so it cannot pass as a person |
| `aud` | none | the product's `JwtAudience` (the built-in product uses `auth-service-tenant`) | `auth-service` (`IdentityTokenService.ServiceAudience`) |
| Other claims | `jti`, `iat`, `nbf`, `exp`, `iss` | `tid` (tenant), `permissions` (array of `key:Level`), plus the standard ones | `token_use` = `service`, `app_id`, `client_id`, plus the standard ones |
| Lifetime | 15 min | 15 min | 10 min |
| Checked by | the Auth Service, policy `IdentityToken` | the product, with the authorization package; or the Auth Service itself when `aud` is `auth-service-tenant` (tenant-admin endpoints) | the Auth Service, policy `ServiceToken` |
| Renewed by | refresh token | exchanging again | asking again with the client secret |

### How it is implemented

Where each token is created and accepted:

| Where | Auth required | Policy / code | What it accepts |
|---|---|---|---|
| Identity endpoints such as `GET /api/auth/users/me`, and `POST /api/auth/token/exchange` | identity token | policy `IdentityAuthentication.PolicyName` = `IdentityToken` | `sub` present and **no** `aud` claim at all |
| `/api/auth/admin/*` | identity token of a platform admin | policy `PlatformAdmin` (`PlatformAdminRequirement`) | as above, plus the platform-admin flag |
| Endpoints a product calls with its own credentials (for example `POST /api/auth/apikeys/validate`) | service token | policy `ServiceToken` (`IdentityAuthentication.ServicePolicyName`) | `token_use=service`, `aud=auth-service`, a parseable `app_id` |
| Tenant-admin endpoints (roles, members, API keys, ...) | product token for the built-in product | `TenantAuthorization.TenantContext` / `RequireTenantPermission` in `src/AuthService.Api/Tokens/TenantPermissions.cs` | `aud=auth-service-tenant`, a `tid` and a `sub`, plus a `permissions` entry at the required level |
| A product's own API | product token for that product | the authorization package (see section 5) | `aud` equal to that product's own audience |

How the Auth Service checks a bearer token (`AddIdentityTokenAuthentication` in `src/AuthService.Api/Tokens/IdentityAuthentication.cs`):

1. `UseAuthentication` runs the JWT bearer handler. It requires a signature, `ValidateIssuerSigningKey`, only `ES256`, the issuer
   `Auth:Tokens:Issuer`, and lifetime with a 30 second clock skew. `MapInboundClaims = false`, so claim names stay `sub`, `tid`, `aud`.
2. The signing key is chosen by the token's `kid` from the keys currently published (`SigningKeyService.GetValidationKeys`). A token
   with no `kid`, or a `kid` that is not published, fails.
3. `ValidateAudience` is **false** in this handler. The audience is policed by the policies above instead: the identity policy rejects
   any token that has an `aud`, the service policy requires `auth-service`, the tenant policy requires `auth-service-tenant`.
4. A failed authentication is a 401 problem with `code` = `unauthorized` and a `WWW-Authenticate: Bearer` header (`OnChallenge`).
5. A token that authenticates but fails a policy is a 403. No custom forbidden handler exists in `src/AuthService.Api` (searched for
   `OnForbidden`), so ASP.NET's default empty 403 is returned. The authorization package, by contrast, writes a problem body
   (`code` = `forbidden`).

What is in each token is built in `IdentityTokenService`: `CreateAccessToken` (only `sub` and `jti`), `CreateServiceToken`
(`ServiceClaims.TokenUse`, `ServiceClaims.ApplicationId`, `ServiceClaims.ClientId`), `CreateProductToken`
(`ProductClaims.TenantId` = `tid`, `ProductClaims.Permissions` = `permissions`). The reserved audience `auth-service` cannot be
registered as a product audience (`RegisterApplicationService` rejects it as "reserved"), so a product token can never carry the
service audience.

Data: tokens are not stored. Signing keys are in `signing_keys` (section 3). Product audiences are in `applications.jwt_audience` (unique).

Libraries: `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.IdentityModel.JsonWebTokens` (`JsonWebTokenHandler`),
`System.Security.Cryptography.ECDsa` (P-256).

Background workers: none for tokens themselves; the signing-key worker is in section 3.

### Configuration and environment

| Setting (config form / environment form) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:Issuer` / `Auth__Tokens__Issuer` | `AuthService` | `IdentityTokenOptions.Issuer` | Must equal the `Issuer` every product configures. Changing it makes every existing token and every product reject tokens. |
| `Auth:Tokens:AccessTokenMinutes` / `Auth__Tokens__AccessTokenMinutes` | 15 | `IdentityTokenOptions` | Identity-token life. |
| `Auth:Tokens:ProductTokenMinutes` / `Auth__Tokens__ProductTokenMinutes` | 15 | `IdentityTokenOptions` | Product-token life, and so how long a permission change or suspension can be ignored by a product. Also the `expiresIn` returned by exchange (minutes x 60). |
| `Auth:Tokens:ServiceTokenMinutes` / `Auth__Tokens__ServiceTokenMinutes` | 10 | `IdentityTokenOptions` | Service-token life. |
| `Auth:Tokens:RefreshTokenDays` / `Auth__Tokens__RefreshTokenDays` | 30 | `IdentityTokenOptions` | Refresh-token life. |

The signing-key settings are in section 3. No setting is a secret in this section.

### Failure modes and production debugging

- **401 `unauthorized`** (empty or unreadable bearer, expired, wrong issuer, unknown `kid`, wrong algorithm, bad signature). The body says
  nothing about why. The Auth Service does not log the reason at its own level; raise the log level of
  `Microsoft.AspNetCore.Authentication.JwtBearer` to see the validation exception.
- **403** on an identity endpoint with a product token or service token: they carry `aud`, which the identity policy refuses. This is
  correct, not a fault.
- **403** on a tenant-admin endpoint with an identity token: an identity token has no `aud=auth-service-tenant`, no `tid`. The caller
  must exchange first (section 2) for the built-in product (`BuiltInApplication.Id`).
- **Clock problems**: the skew allowed is 30 seconds. A server clock minutes out of step makes fresh tokens "not yet valid" or "expired".
- To inspect a token without trusting it, decode the three parts: header (`kid`, `alg`), payload (`aud`, `tid`, `exp`).
- Metric: `auth_http_requests_total{route=...,status="401"}` rising on one route points at a client or clock problem.

### Decisions and open questions

- Three token types rather than one: decision 0004 (one global identity, tenant chosen at exchange) and 0005 (the Auth Service is a
  product of its own, so its tenant-admin token is an ordinary product token).
- A product token is not revocable. Lowering the lifetime shortens the window but costs more exchanges.
- **Unknown:** the exact body of a 403 from the Auth Service's own policies. No handler is registered, so it is probably empty; the tests
  only assert the status code. Confirm by calling a tenant endpoint with an identity token.
- Inconsistency (minor): the Auth Service's `ClaimsPrincipalExtensions.GetTenantId` accepts an all-zero GUID, while the package's
  `AuthServiceClaims.GetTenantId` rejects it. An all-zero `tid` is never issued, so this has no effect today.

---

## Token exchange

### What it does

A signed-in person trades their identity token for a **product token** for one organisation and one product. This is the only way
into a product, and also the way a tenant admin gets a token to use the console's administration screens (the built-in product).
It exists because one person can belong to several organisations, so login cannot choose the organisation; exchange does.

Rules (all read fresh from the database at every exchange, so a suspension or removed seat applies at the next exchange):

- The caller must be an **Active** member of the tenant. Invited, Disabled or no membership is refused.
- The product must exist and be active (`applications.is_active`).
- The tenant must have an **Active** subscription to the product, and the member must hold an access grant (a seat) for it. The
  Auth Service's own built-in product (`applications.is_built_in`) needs neither: every Active member may get a token for it.
- The permissions in the token are the highest level the member holds for each permission of that product, from all their roles.
  Level `None` is left out. There are no deny rules.
- The token lives 15 minutes (`Auth:Tokens:ProductTokenMinutes`).
- A person may exchange at most 30 times a minute (`Auth:TokenExchange:UserLimit`).
- Refusals are deliberately identical, except one (a suspended tenant, TOK-05).

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `POST /api/auth/token/exchange` | identity token (policy `IdentityToken`) and the shared rate limit policy `token-exchange` | `TokenExchangeEndpoints.ExchangeAsync` | `TokenExchangeService.ExchangeAsync` | Validates the body, applies the checks below, returns a product token |

Request body: `{ "tenantId": "<guid>", "applicationId": "<guid>" }`. Reply 200: `{ "accessToken": "...", "tokenType": "Bearer", "expiresIn": 900 }`
with `Cache-Control: no-store`.

Flow, in order:

1. `UseAuthentication` validates the identity token. A product or service token passes authentication but fails the `IdentityToken`
   policy (403). No token is 401.
2. `SharedRateLimitMiddleware` runs **after** authentication and **before** authorization and counts one hit under the key
   `token-exchange|user:<userId>` (`RateLimitPolicies.Resolve`). If the caller has no usable `sub` the key is `token-exchange|ip:<address>`.
   Hit number 31 in the window gets 429 `rate_limited` with `Retry-After` and the endpoint is not run.
3. `ExchangeAsync` (endpoint) checks the body: a missing or all-zero `tenantId` or `applicationId` gives 400 (`ValidationProblem`
   with `TenantId` / `ApplicationId` errors).
4. The user id comes only from the verified token (`caller.GetUserId()`), never from the body.
5. `TokenExchangeService.ExchangeAsync` then makes these checks, each one reading the database:
   1. Membership join: `tenant_memberships` where `tenant_id` and `user_id` match and `status = 'Active'`, joined to `tenants` to see
      whether `tenants.status = 'Suspended'`. No row gives `Denied`.
   2. If the tenant is suspended, return `TenantSuspended`. This is checked **before** the product is looked at, so an active
      member of a suspended tenant gets `tenant_suspended` even for an invented product id.
   3. Product lookup: `applications` where `id` matches and `is_active`. None gives `Denied`. It reads `jwt_audience` and `is_built_in`.
   4. If the product is not built in: a `subscriptions` row for the tenant and product with `status = 'Active'`; otherwise `Denied`.
   5. If the product is not built in: an `application_access` row for (`tenant_id`, `membership_id`, `application_id`); otherwise `Denied`.
   6. `PermissionResolver.ResolveAsync` works out the permissions (below).
   7. `IdentityTokenService.CreateProductToken` signs the token; permissions with level above `None` are written as `key:Level`,
      sorted by key with ordinal comparison.
6. The endpoint sets `Cache-Control: no-store` and maps the result: `Issued` is 200, `TenantSuspended` is 403 `tenant_suspended`
   ("Organisation suspended"), anything else is 403 `exchange_denied` ("Access denied").

Permission computation (`PermissionResolver`, `src/AuthService.Api/Features/TokenExchange/PermissionResolver.cs`), roles to permissions by level:

- If the membership holds the built-in **Owner** role (`roles.is_built_in` and `roles.name = Role.OwnerName`), every permission in the
  product's catalogue (`permissions.application_id`) is given `Full`. No grant rows are stored for the Owner, so a permission added to
  the catalogue later is in the Owner's next token (test `TOK01_APermissionAddedToTheCatalogLaterIsInTheOwnersNextToken`).
- Then, for every role the member holds (`membership_roles` joined to `role_permissions` joined to `permissions` for this product only),
  the grant's `level` is merged in: the highest level per permission wins.
- Levels are ordered `None`(0) < `ReadOnly`(1) < `CreateRead`(2) < `Full`(3) (`AccessLevel`).
- Only this product's permissions can appear. Permissions of other products, and of other tenants' roles, are never read
  (the join is on `tenant_id`).

Why the refusals look the same: `Denied` has one response (`exchange_denied`) for no membership, Invited or Disabled membership, unknown
product, inactive product, no active subscription (Suspended or Cancelled), no seat, and an unknown or foreign tenant. Test
`TOK01_EveryRefusalLooksTheSameSoNothingIsRevealed` compares the bodies of three refusals and requires one distinct body. The only
exception is `tenant_suspended`, because that caller is already an Active member and so already knows the tenant exists and is theirs;
anyone else, including a Disabled member of a suspended tenant, still gets `exchange_denied` (tests in `Tok05`).

Refusal table:

| Case | Status | `code` |
|---|---|---|
| No or bad identity token | 401 | `unauthorized` |
| Product, service or other audience-bearing token | 403 | (empty 403 from the policy) |
| Missing or all-zero `tenantId` / `applicationId` | 400 | validation problem |
| Not an Active member, unknown tenant, unknown or inactive product, no Active subscription, no seat | 403 | `exchange_denied` |
| Active member of a Suspended tenant | 403 | `tenant_suspended` |
| More than the limit in the window | 429 | `rate_limited` (with `Retry-After`) |
| Rate-limit store (the database) unreachable | 503 | `rate_limit_unavailable` (with `Retry-After: 5`) |

Data read: `tenant_memberships` (`MembershipStatus`: Invited, Active, Disabled), `tenants` (`TenantStatus`: Trial, Active, Suspended; a
Trial tenant can exchange), `applications`, `subscriptions` (`SubscriptionStatus`: Active, Suspended, Cancelled), `application_access`,
`membership_roles`, `roles`, `role_permissions`, `permissions`. Nothing is written. Counters are written to `rate_limit_counters`
(`key_hash`, `hits`, `expires_at`).

Per-person rate limit: a fixed window kept in `rate_limit_counters` by `PostgresRateLimitStore` (default store) so all instances share it.
Refused exchanges count too, so guessing tenant or product ids is limited. An exchange for the built-in product shares the same
budget (test `OnlyExchangeIsLimited_AndEveryExchangeOfAPersonSharesOneBudget`).

Background workers: `RateLimitCleanupWorker` deletes expired counters every `Auth:RateLimit:CleanupSeconds` (60). If it does not run,
expired counters are still replaced on next use, so limits stay correct; the table only grows.

### Configuration and environment

| Setting (config form / environment form) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:TokenExchange:UserLimit` / `Auth__TokenExchange__UserLimit` | 30 | `TokenExchangeOptions` | Exchanges per person per window before 429. Too low and the console's picker or admin screens hit 429. |
| `Auth:TokenExchange:UserWindowMinutes` / `Auth__TokenExchange__UserWindowMinutes` | 1 | `TokenExchangeOptions` | Window length. |
| `Auth:Tokens:ProductTokenMinutes` / `Auth__Tokens__ProductTokenMinutes` | 15 | `IdentityTokenOptions` | Token life and `expiresIn`. |
| `Auth:RateLimit:Store` / `Auth__RateLimit__Store` | `Postgres` | `RateLimitOptions` | `Memory` keeps counters per process: with several instances the real limit is limit x instances. A value other than `Postgres` or `Memory` stops startup. |
| `Auth:Proxy:*` / `Auth__Proxy__*` | not set | `ProxyOptions` | Only matters for callers without a valid token (counted per IP): behind a proxy that is not listed, all of them share the proxy's address. |

### Failure modes and production debugging

- **What the caller sees:** see the refusal table. The console maps these to its own messages (`tenant_suspended`, `not_available`,
  `rate_limited`, `expired`, `unavailable`; see `ProductEndpoints.Code` in `src/AuthService.Web/Security/ProductEndpoints.cs`).
- **What is logged:** nothing is logged by `TokenExchangeService`, and **no audit entry is written for an exchange** (no
  `AuditActions` constant exists for it). The only log line on this path is the 503 case from `SharedRateLimitMiddleware`:
  "The rate-limit store could not be reached, so a request to a limited endpoint was refused ({Policy})." with policy `token-exchange`.
  Use `auth_http_requests_total{route="/api/auth/token/exchange",status="403"|"429"|"200"}` from `/metrics` to see the rates.
- **Half-states and edge cases:**
  - A seat (`application_access`) without a subscription is refused; a subscription without a seat is refused. Both must exist.
  - A member who holds roles with grants but no seat is refused (test `TOK_TC16`).
  - A member with a seat but no roles gets a valid token with an empty `permissions` array. The product then answers 403 on any
    permission-protected endpoint.
  - A role whose grants are for another product contributes nothing.
  - A platform admin cannot exchange for a tenant they are not an Active member of (test `APP_TC11`).
- **Most likely production causes:**

| Symptom | Likely cause | How to confirm |
|---|---|---|
| 403 `exchange_denied` for someone who "should" have access | Seat missing, membership Invited/Disabled, subscription Suspended/Cancelled, product inactive | SQL below |
| 403 `tenant_suspended` | The tenant is Suspended | `SELECT status FROM tenants WHERE id = '<tenant>';` |
| 429 `rate_limited` | A client loop, or the limit is low, or several instances on `Memory` | `Retry-After` header; metric counts; `Auth__TokenExchange__UserLimit` |
| 503 `rate_limit_unavailable` | Database unreachable or `rate_limit_counters` missing (migration not applied) | log line above; `\d rate_limit_counters` |
| Token has no or too few permissions | Roles not assigned for **this product**, or the role grants are for another product | SQL below |
| Permissions out of date after a role change | Old token still valid; exchange again | token `exp` claim |

Read-only SQL for "why is this person refused":

```sql
-- membership and tenant standing
SELECT m.id AS membership_id, m.status AS membership_status, t.status AS tenant_status
FROM tenant_memberships m JOIN tenants t ON t.id = m.tenant_id
WHERE m.tenant_id = '<tenant>' AND m.user_id = '<user>';
-- product, subscription and seat
SELECT a.is_active, a.is_built_in, a.jwt_audience,
       (SELECT s.status FROM subscriptions s WHERE s.tenant_id = '<tenant>' AND s.application_id = a.id) AS subscription_status,
       EXISTS (SELECT 1 FROM application_access x WHERE x.tenant_id = '<tenant>' AND x.membership_id = '<membership>' AND x.application_id = a.id) AS has_seat
FROM applications a WHERE a.id = '<application>';
-- what the roles grant for this product
SELECT p.key, rp.level, r.name AS role
FROM membership_roles mr
JOIN roles r ON r.id = mr.role_id
JOIN role_permissions rp ON rp.tenant_id = mr.tenant_id AND rp.role_id = mr.role_id
JOIN permissions p ON p.id = rp.permission_id
WHERE mr.tenant_id = '<tenant>' AND mr.membership_id = '<membership>' AND p.application_id = '<application>';
```

The counter table stores only a hash of the key, so you cannot look up one person's counter. To see whether limits are being hit,
`SELECT hits, expires_at FROM rate_limit_counters ORDER BY hits DESC LIMIT 10;`.

### Decisions and open questions

- Identical refusals reveal nothing to outsiders; the suspended-tenant exception reveals nothing new to an Active member (TOK-05).
- The limit is per person, not per address, so people behind one office address do not block each other (`RateLimitPolicies`).
- The built-in product needs no subscription or seat (decision 0005), so any Active member can always get a tenant-admin token;
  what they may do depends only on roles.
- The check order has one visible effect: tenant suspension is reported before product problems.
- Risk: a permission is checked in the **product token**, not live. A product honours a role removal only after the old token expires
  (up to 15 minutes), and nothing tells the product earlier. This is documented behaviour (tests `SUB_TC6`, `TOK04_RoleDowngradeTests`).
- **Assumption (confirm):** exchange is not audited by design (it would be very high volume). No code or comment says so; the absence
  of an `AuditActions` entry is the only evidence.

---

## Signing keys and the JWKS

### What it does

The Auth Service signs every token with a private key it creates and looks after itself. Products check tokens with the matching
**public** keys, which the Auth Service publishes at `GET /.well-known/jwks.json` with no login. Keys change on a schedule without
anyone doing anything, and in a hurry (emergency rotation) if a key might have leaked.

Rules and numbers (all in `IdentityTokenOptions`):

- Key type: ECDSA P-256, signature ES256. The key id (`kid`) is the key's RFC 7638 thumbprint, so the same public key always has the same id.
- A key signs for **90 days** (`SigningKeyRotationDays`).
- The next key is **published 24 hours before** it starts signing (`SigningKeyPrePublishHours`), so every product learns it first.
- The old key **stays published 24 hours after** it stops signing (`SigningKeyPostRetireHours`), so tokens issued just before the switch
  still validate.
- Each instance reloads keys from the database every **60 seconds** (`SigningKeyRefreshSeconds`).
- The private half is stored encrypted (AES-256-GCM) under `Auth:Tokens:KeyEncryptionKey`. A leaked database alone cannot forge tokens.
- The JWKS is sent with `Cache-Control: public, max-age=300`.
- **The discovery document (ONB-03)** at `GET /.well-known/auth-service-configuration`, also with no login and `public, max-age=300`, names
  what a product needs to start from one address: `issuer` (`Auth:Tokens:Issuer`), `apiBaseUrl`, `jwksUrl`, `tokenUrl` and `consoleUrl`
  (`Email:PublicBaseUrl`, null when empty). `apiBaseUrl` is `Auth:Discovery:PublicBaseUrl` when that is an absolute http or https address
  (a trailing slash trimmed), else the scheme and host the request came to; set it when the API is behind a proxy or is called by an
  internal name, as the web console calls it, so the document never carries an internal address. It holds nothing secret
  (`DiscoveryEndpoints`, `DiscoveryDocument`).

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `GET /.well-known/jwks.json` | none (`AllowAnonymous`) | `JwksEndpoints.GetJwks` | `SigningKeyService.GetPublishedKeys` | Returns `{ "keys": [ { kty, crv, use, alg, kid, x, y } ] }`; never private material |
| `POST /api/auth/admin/signing-keys/rotate` | platform admin identity token | `SigningKeyAdminEndpoints.RotateAsync` | `SigningKeyService.RotateNowAsync` -> `SigningKeyRotation.RotateNowAsync` | Emergency rotation; returns `{ "keyId": "<new kid>" }` |

Classes (all in `src/AuthService.Api/Tokens`):

- `SigningKey` entity (`src/AuthService.Infrastructure/Persistence/Entities/SigningKey.cs`), table `signing_keys`: `id`, `key_id` (unique,
  max 64), `public_key_x`, `public_key_y`, `encrypted_private_key`, `created_at`, `publish_from`, `sign_from`, `sign_until` (null while
  newest with no successor), `publish_until` (null while still published).
- `KeyEncryption`: AES-256-GCM. Stored form is base64 of nonce (12 bytes) + tag (16 bytes) + ciphertext. A wrong secret or tampered value
  throws instead of returning junk.
- `SigningKeyRotation` (scoped, uses the database): `EnsureAsync` and `RotateNowAsync`.
- `SigningKeyService` (singleton): holds the in-memory key set and answers the three questions below.
- `SigningKeyWorker` (hosted service): calls `RefreshAsync` on a timer.

A key's life: `publish_from` (in the JWKS) <= `sign_from` (starts signing) < `sign_until` (stops signing) <= `publish_until` (leaves the JWKS).

Key creation and scheduled rotation (`SigningKeyRotation.EnsureAsync`), run inside a transaction holding a PostgreSQL advisory lock
(`pg_advisory_xact_lock(7270001)`, constant `AdvisoryLockId`), so several instances cannot each create a key:

1. If `signing_keys` is empty: create a key with `publish_from = sign_from = now`. Audit `auth.signing_key.created`
   (`AuditActions.SigningKeyCreated`), actor System, detail `initial`.
2. Otherwise find the **current** key (latest `sign_from` among keys with `sign_from <= now` and `sign_until` null or in the future).
3. If a key with a later `sign_from` already exists, the next is already scheduled: do nothing.
4. Else, `rotateAt = current.sign_from + SigningKeyRotationDays`. When `now >= rotateAt - prePublish`:
   `signFrom = max(rotateAt, now + prePublish)`; set `current.sign_until = signFrom` and `current.publish_until = signFrom + postRetire`;
   add a new key with `publish_from = now`, `sign_from = signFrom`. Audit `auth.signing_key.rotated`, actor System, detail `scheduled`.
5. If rotation is **overdue** (service was down), the new key still waits the full pre-publish time before it signs (test
   `TOK02_Rotation_AnOverdueRotationStillWaitsTheFullPrePublishPeriod`).

Emergency rotation (`RotateNowAsync`, under the same lock): every existing key gets `sign_until` and `publish_until` set to now if they
were later or null (this also discards a key that was published for later), a new key is added with `publish_from = sign_from = now`,
audit `auth.signing_key.rotated` with the admin as actor and detail `emergency`. The service then reloads at once and logs the warning
"Signing keys were rotated immediately (emergency rotation)." Tokens signed by the old keys stop validating at the Auth Service
immediately.

The in-memory key set (`SigningKeyService`):

- `_entries` is a `volatile KeyEntry[]` replaced as a whole on each load. `LoadAsync` reads rows where `publish_until` is null or in the
  future. A key that has left the JWKS is not kept.
- Only a key that is signing or will sign (`sign_until` null or in the future) has its private half decrypted (`CreatePrivateKey` via
  `KeyEncryption.Decrypt`); others keep only the public key. Already-decrypted keys are reused across reloads.
- Answers are worked out **when asked**, from the clock (`TimeProvider`), not when loaded:
  - `CurrentSigningKey`: the entry with the latest `sign_from` that is signing now. Throws `InvalidOperationException` ("There is no
    signing key to sign with. The signing keys have not been loaded.") if there is none.
  - `GetValidationKeys(kid)`: the published key with that `kid`; nothing for a missing or unknown `kid`.
  - `GetPublishedKeys()`: entries with `publish_from <= now` and `publish_until` null or in the future, oldest first.
- `RefreshAsync` takes a semaphore, runs `EnsureAsync`, then `LoadAsync`.

At start-up (`Program.cs`): right after `builder.Build()`, `SigningKeyService.RefreshAsync` is awaited before anything else, so a missing
key-encryption secret, an unreadable key, an unreachable database or a missing table stops the deployment instead of failing the first
login. Then `BuiltInApplicationSync` runs. The worker waits one interval before its first reload, because the start-up load counts as the first.

Worker: `SigningKeyWorker` waits `max(1, SigningKeyRefreshSeconds)` seconds, then calls `RefreshAsync`, in a loop. On an exception it logs
"Refreshing the signing keys failed; trying again shortly." and keeps the keys already in memory. If the worker is not running on an
instance, that instance never learns of keys scheduled by another instance or by itself later; once its in-memory current key passes
`sign_until` it has no key to sign with (see failure modes).

Readiness and metrics: `GET /health/ready` is 503 with `"signingKey":"failed"` when `SigningKeyService.HasCurrentKey` is false, and logs
"Not ready: there is no signing key that signs right now." `/metrics` exposes `auth_signing_keys_published` (keys in the JWKS now) and
`auth_signing_key_age_seconds` (how long the current key has signed).

### Configuration and environment

| Setting (config form / environment form) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:KeyEncryptionKey` / `Auth__Tokens__KeyEncryptionKey` (**secret, required outside Development and Testing**) | none | `IdentityTokenOptions`, read by `KeyEncryption` | Base64 of exactly 32 bytes (`openssl rand -base64 32`). Missing, not base64, or the wrong length stops startup with "Auth:Tokens:KeyEncryptionKey is not configured." / "is not valid base64." / "must be 32 bytes (base64 of 32 random bytes)." In Development and Testing a fixed, public key is used instead. Changing it makes stored keys unreadable; re-encryption is not built. In the Docker deployment it comes from `AUTH_KEY_ENCRYPTION_KEY` (`deploy/docker-compose.yml`). Every instance must have the same value. |
| `Auth:Tokens:SigningKeyRotationDays` / `Auth__Tokens__SigningKeyRotationDays` | 90 | `IdentityTokenOptions` | How long a key signs. |
| `Auth:Tokens:SigningKeyPrePublishHours` / `Auth__Tokens__SigningKeyPrePublishHours` | 24 | `IdentityTokenOptions` | How early a new key is published. Must exceed the longest time a product keeps a stale key set (default package cache 10 minutes). |
| `Auth:Tokens:SigningKeyPostRetireHours` / `Auth__Tokens__SigningKeyPostRetireHours` | 24 | `IdentityTokenOptions` | How long a retired key still validates. Must exceed the longest token life (15 minutes). |
| `Auth:Tokens:SigningKeyRefreshSeconds` / `Auth__Tokens__SigningKeyRefreshSeconds` | 60 | `IdentityTokenOptions` | How often each instance reloads; also how quickly another instance's emergency rotation is seen. |

### Failure modes and production debugging

- **Service will not start:** the start-up `RefreshAsync` throws. Causes: key-encryption secret missing or malformed (messages above);
  wrong secret for the stored keys (a cryptographic exception from `AesGcm.Decrypt` while loading the current key); database unreachable;
  migrations not applied so `signing_keys` does not exist.
- **Everything 401 right after a restart or restore:** the database was replaced or reset, so the keys are new and old tokens carry an
  unknown `kid`. Fresh logins work. Also check that every instance uses the same database and the same `KeyEncryptionKey`.
- **Products reject tokens right after a scheduled rotation:** should not happen, because of the 24 hour pre-publish. If it does, the
  product's own JWKS cache is stuck or it cannot reach the JWKS URL (section 5).
- **Products keep accepting old-key tokens after an emergency rotation:** a product refreshes its key set every `KeyCacheMinutes` (10)
  or when it meets a token with an unknown `kid`; until then it still trusts the old key (test
  `TOK03_ARotatedOutKeyStopsValidatingOnceTheProductHasRefreshed` shows it only drops the old key after it has refreshed).
- **Other Auth Service instances after an emergency rotation:** each instance sees the change at its next reload (up to 60 s). An
  instance that has not reloaded still signs with the old key.
  **Assumption (confirm):** during that window tokens signed by a lagging instance fail validation on an instance that already rotated.
  This follows from the code (`FindCurrent` and `GetValidationKeys` use the instance's own loaded set) and is not covered by a test.
- **No key signs now:** `CurrentSigningKey` throws, so login and exchange return 500, `GET /health/ready` is 503, and the log has
  "Not ready: there is no signing key that signs right now." Causes: the worker is not running and the instance has outlived its key's
  `sign_until`; or the table has keys but none with `sign_from <= now < sign_until` (for example edited by hand). `EnsureAsync` only
  schedules a successor when a current key exists, so it will **not** repair this by itself; restarting does not help either if the rows
  are inconsistent.
- **Refresh failures:** log "Refreshing the signing keys failed; trying again shortly." Keys in memory keep working.
- **Audit:** `auth.signing_key.created` (detail `initial`), `auth.signing_key.rotated` (detail `scheduled` or `emergency`).

Read-only SQL:

```sql
SELECT key_id, publish_from, sign_from, sign_until, publish_until, created_at
FROM signing_keys ORDER BY sign_from;

SELECT occurred_at, action, actor_type, actor_id, detail
FROM audit_entries WHERE action LIKE 'auth.signing_key.%' ORDER BY occurred_at DESC LIMIT 20;
```

Healthy shape: one row signing now (`sign_from <= now()` and `sign_until` null or later); during a rotation window two or more rows
with overlapping publish times. Compare with `GET /.well-known/jwks.json`: the `kid` values there must equal the rows with
`publish_from <= now()` and `publish_until` null or later.

### Decisions and open questions

- Keys live in the database so every instance agrees; a PostgreSQL advisory lock prevents duplicate first keys (test
  `TOK02_Rotation_WorkersRunningAtTheSameTimeOnAnEmptyTableCreateExactlyOneKey`).
- The 24 hour pre-publish and post-retire windows are generous compared with the 15 minute token life and 10 minute product cache;
  they cover a service outage over the switch.
- The JWKS header says `max-age=300`, while the package refreshes on its own `KeyCacheMinutes` timer (default 10). The root
  `README.md` section says products cache "5 minutes". These do not match; harmless because of the 24 hour pre-publish.
- Risk: losing `KeyEncryptionKey` loses every stored signing key. Rotation of that secret (re-encrypting) is stated as not built in
  `README.md` ("Signing keys and the JWKS"). New keys could be created after a loss, but all existing tokens would be rejected.
- Risk: no automatic repair when no key is signing (above).
- The JWKS endpoint has no rate limit and no authentication, by design (`JwksEndpoints` comment). It is cheap (in-memory).
- **Unknown:** whether the reverse proxy (`deploy/Caddyfile`) caches the JWKS. Not checked; it does not matter for correctness.

---

## API keys

### What it does

An API key lets a tenant's own backend call a product with no person logging in. A tenant admin creates a key, chooses which product it
works for and exactly which permissions (and levels) it has, and gets the raw key **once**. The product asks the Auth Service whether a
presented key is genuine and what it allows. A key can be rotated with an overlap so the tenant can swap it without downtime, and
revoked at once.

Rules:

- Created with a tenant-admin token holding `apikeys.manage` at `Full` (the Owner has it). Listing needs `apikeys.read` (ReadOnly).
- The key belongs to the caller's tenant (from the token's `tid`), never from the request.
- Scope is fixed at creation: one product (`ApplicationId`) and 1 to 200 permissions of that product, each at a level. A missing level
  means `Full`. A key with **no** product and no permissions is allowed; it only identifies the tenant and grants nothing.
- The product must be one the tenant has an Active subscription to, and not the Auth Service's own product.
- **A key can never have more than the person making it**: each requested permission must be within what the creator's own roles grant
  (`PermissionResolver`), otherwise 403 `scope_exceeds_permissions`.
- Name: required, trimmed, at most 100 characters (`ApiKeyValidator.MaxNameLength`).
- The raw key is `ak_{keyId}.{secret}`: `keyId` is 24 lowercase hex characters and is not secret; the secret is 256 random bits,
  base64url. Only a salted SHA-256 of the secret is stored. The raw key is shown once, never logged or audited.
- Rotation returns a new raw key with the same scope; the old key works for a grace period (default 24 hours, 0 to 720 hours), then stops.
- Revoking ends a key at once and for good.
- Statuses shown: `Active`, `Expiring` (being rotated out), `Expired`, `Revoked` (`ApiKeyService.StatusOf`).

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `POST /api/auth/apikeys` | tenant-admin token, `apikeys.manage` at `Full` | `ApiKeyEndpoints.CreateAsync` | `ApiKeyValidator.TryValidate`, `ApiKeyService.CreateAsync` | Creates a key; 201 with `{ key, rawKey }`, `Cache-Control: no-store` |
| `GET /api/auth/apikeys` | `apikeys.read` at `ReadOnly` | `ListAsync` | `ApiKeyService.ListAsync` | One page, newest first (default 50, max 200 per page) |
| `GET /api/auth/apikeys/{keyId}` | `apikeys.read` at `ReadOnly` | `GetAsync` | `ApiKeyService.GetAsync` | One key, masked; 404 if not in this tenant |
| `DELETE /api/auth/apikeys/{keyId}` | `apikeys.manage` at `Full` | `RevokeAsync` | `ApiKeyService.RevokeAsync` | Revokes; 204; again is a no-op |
| `POST /api/auth/apikeys/{keyId}/rotate` | `apikeys.manage` at `Full` | `RotateAsync` | `ApiKeyService.RotateAsync` | New key with same scope; 201 `{ key, rawKey, previousKeyExpiresAt }` |
| `POST /api/auth/apikeys/validate` | a product's **service token** (policy `ServiceToken`) | `ValidateAsync` | `ApiKeyValidationService.ValidateAsync` | Says what a presented key is |

Create flow (`ApiKeyService.CreateAsync`):

1. The endpoint runs `ApiKeyValidator.TryValidate`: name; product id not all-zero; permissions require a product and a product needs at
   least one; at most 200; each permission key trimmed and lower-cased, a level of `None`, `ReadOnly`, `CreateRead` or `Full` (names only,
   numbers refused), no duplicates. Failures are 400 with field errors, and nothing is created.
2. If a product is given: look it up joined to an Active `subscriptions` row for the tenant, excluding `is_built_in`. Not found, not
   subscribed and the built-in product all give the same 400 error on `ApplicationId`.
3. Load the product's permission catalogue; an unknown permission key is a 400 error on `Permissions[i]`.
4. Find the caller's Active membership; `PermissionResolver.ResolveAsync` gives what they hold; any requested permission that they do not
   hold, or hold at a lower level, makes the whole request 403 `scope_exceeds_permissions` ("A key cannot have more than you do. You do not
   hold: ...").
5. `ApiKeySecrets.Generate` makes the key id, secret and 16-byte salt; the hash is `SHA256(salt || secret)`, base64.
6. Insert one `api_keys` row and one `api_key_permissions` row per permission; record `auth.apikey.created` (`AuditActions.ApiKeyCreated`)
   with the key's id as detail; save. Reply with the raw key.

Rotate flow (`RotateAsync`): in a transaction, `SELECT * FROM api_keys WHERE id = ... AND tenant_id = ... FOR UPDATE`; missing is 404;
revoked or already expired is 409 `api_key_not_active`. `graceEnd = now + graceHours` (request value, else `Auth:ApiKeys:RotationGraceHours`);
the old key's `expires_at` is set to `graceEnd` **only if** it is null or later (rotating again never extends an older key's life).
A new `api_keys` row (same name, product; creator is the person rotating) and copies of every `api_key_permissions` grant are added, and
`auth.apikey.rotated` is recorded with the **new** key's id. `graceHours` outside 0 to 720 (`ApiKeyValidator.MaxGraceHours`) is 400.
`0` ends the old key immediately.

Revoke: sets `revoked_at` and records `auth.apikey.revoked` once; a second revoke records nothing (test
`KEY02_RevokingTwiceIsANoOpAndIsAuditedOnce`). A key of another tenant is 404, never 403.

Validate flow (`ApiKeyValidationService.ValidateAsync`, called with the product's service token; the product is `caller.GetApplicationId()`
from the token):

1. `ApiKeySecrets.TryParse`: must start with `ak_`, have the dot at position 27, a 24-character lowercase hex id and a secret of 1 to
   100 characters. Otherwise `Invalid`.
2. Look up the row by `api_keys.key_id`. Compute the hash comparison **even when no row is found** (a dummy salt and hash), in constant
   time (`CryptographicOperations.FixedTimeEquals`), so timing says little.
3. `Invalid` (401 `invalid_api_key`, "The API key is not valid.") when: no row, wrong secret, `revoked_at` set, or `expires_at <= now`.
4. Also `Invalid` when the tenant is Suspended (`tenants.status`), or the key was made for a **different** product than the caller.
5. For a key with a product: the tenant must still have an Active subscription, otherwise `Forbidden` 403 `subscription_not_active`.
   The key's permissions are loaded.
6. If the request names a `permission`: it is lower-cased and must be in the key's scope at the requested `level` (default `ReadOnly`) or
   higher, otherwise 403 `outside_key_scope` ("This key does not allow that action."). A key with no product has no scope, so any named
   permission fails.
7. 200 returns `{ tenantId, keyId, name, applicationId, permissions: [ { permission, level } ] }` with `Cache-Control: no-store`. The
   product must use that tenant and nothing else.

Hashing and masking: `ApiKeySecrets` (`src/AuthService.Api/Features/ApiKeys/ApiKeySecrets.cs`). The mask shown in lists is
`ak_` + first 8 hex characters + `...` (`ApiKeySecrets.Mask`). A fast salted hash is used on purpose because the secret is 256 random
bits (stated in the class comment).

Data: `api_keys` (entity `ApiKey`): `id`, `tenant_id`, `application_id` (nullable), `name` (100), `key_id` (24, unique), `secret_salt`,
`secret_hash`, `created_by_user_id`, `created_at`, `expires_at`, `revoked_at`. `api_key_permissions` (entity `ApiKeyPermission`): `id`,
`tenant_id`, `api_key_id`, `permission_id`, `level` (stored as text), unique on (`api_key_id`, `permission_id`). The grant's foreign key
is (`tenant_id`, `api_key_id`) -> `api_keys` (`tenant_id`, `id`), so the database itself rejects a grant from another tenant.
State: Active -> Expiring (when rotated out; `expires_at` set) -> Expired (`expires_at` passed); any -> Revoked.

Libraries: EF Core (raw SQL only for the `FOR UPDATE` lock), `System.Security.Cryptography`. No external service. No background worker:
expiry is evaluated at validation time.

### Configuration and environment

| Setting (config form / environment form) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:ApiKeys:RotationGraceHours` / `Auth__ApiKeys__RotationGraceHours` | 24 | `ApiKeyOptions` | Default overlap when a rotation request gives no `graceHours`. |

Constants (not settings): name 100 characters, 200 permissions, grace 0 to 720 hours (`ApiKeyValidator`). No secret is configured for
API keys; the product's own client id and secret (see the applications part) are needed to get the service token used for validation.

### Failure modes and production debugging

| What the caller sees | Cause |
|---|---|
| 400 validation problem on create | Name, product, permission or level problem (messages name the field, e.g. "Unknown permission 'x' for this application.", "The application is unknown or the tenant has no active subscription to it.") |
| 403 `scope_exceeds_permissions` on create | The creator lacks a permission or has it at a lower level |
| 403 (empty) on create/list/revoke/rotate | Token lacks `apikeys.manage` / `apikeys.read`, is an identity token, or is the wrong audience |
| 404 `not_found` ("No such API key in this tenant.") | Unknown id, or another tenant's key |
| 409 `api_key_not_active` on rotate | Key revoked or already expired |
| 401 `invalid_api_key` on validate | Malformed, unknown, wrong secret, revoked, expired, tenant Suspended, or made for another product |
| 403 `subscription_not_active` on validate | The tenant's subscription to the product is no longer Active |
| 403 `outside_key_scope` on validate | Permission/level asked is outside the key's scope |
| 403 (empty) on validate | The caller used something other than a product service token |
| 400 on validate | `level` is not `None`, `ReadOnly`, `CreateRead`, `Full` |

Logging and audit: nothing is logged by this feature. Audit actions `auth.apikey.created`, `auth.apikey.rotated`, `auth.apikey.revoked`;
the detail is the key's id (UUID), never the raw key. Test `KEY01_OnlyASaltedHashIsStoredAndTheRawKeyIsNeverLoggedOrAudited` checks the
database, audit and logs for the raw key and secret.

Likely production causes of "the key stopped working": it was rotated and the grace period ended (`expires_at` passed); it was revoked;
the tenant was suspended; the tenant's subscription was suspended or cancelled (that one is a 403, not a 401); the product is validating
with the wrong service token (a key for product A presented by product B is a 401 that reveals nothing); the client sent a different key
format (copy truncated; the masked form `ak_xxxxxxxx...` is not usable).

```sql
SELECT id, tenant_id, application_id, name, key_id, created_at, expires_at, revoked_at
FROM api_keys WHERE key_id = '<24 hex chars after ak_>';

SELECT p.key, k.level
FROM api_key_permissions k JOIN permissions p ON p.id = k.permission_id
WHERE k.api_key_id = '<api_keys.id>';

SELECT occurred_at, action, detail FROM audit_entries
WHERE action LIKE 'auth.apikey.%' AND tenant_id = '<tenant>' ORDER BY occurred_at DESC;
```

Edge cases and half-states:

- Rotation of a key with a past `expires_at` is refused, but a key whose grace has ended is simply `Expired`; create a new one.
- Revoking a key during its grace period ends it at once (test `KEY02_RevokingARotatedOutKeyDuringItsGraceEndsItAtOnce`).
- Two simultaneous rotations both succeed and each makes a working key (row lock, test `KEY02_TwoSimultaneousRotationsEachProduceAWorkingKey`).
- Rotation copies the old scope without re-checking that the person rotating still holds those permissions. The scope can only equal
  the old key's, never grow.
- Disabling or removing the person who created a key does not affect the key (test `KEY_TC8` shows revoking does not affect sessions, and
  keys are separate credentials from logins).

### Decisions and open questions

- The key is looked up by a public id and the secret checked by hash, so a database leak does not reveal usable keys.
- A key for a product is refused by other products as 401, not 403, so a product learns nothing about keys belonging to others.
- Unsubscribed or built-in products and unknown permissions share the same style of 400, so a tenant cannot probe the catalogue.
- **Observation:** a key with **no product** passes `ApiKeyValidationService` for **any** calling product (it only fails if a permission
  is asked for). It carries the tenant id and no scope. Whether a product should accept such a key at all is the product's decision. The
  tests only assert that it "identifies the tenant but grants nothing" (`KEY01_AKeyForNoProductIdentifiesTheTenantButGrantsNothing`).
- **Observation:** `POST /api/auth/apikeys/validate` has no rate limit of its own; it is protected only by needing a service token (whose
  issue is limited to 60 per IP per minute by default: `Auth:Applications:TokenIpLimit`). Each call is a database read plus one hash.
- **Unknown:** whether products cache validation results, and for how long. Nothing in the repository defines it (the response is
  `no-store`). If a product caches, a revoked key keeps working there for that long.
- Status "Expired" for rotated-out keys and a `RevokedAt` that wins over `ExpiresAt` are as coded in `StatusOf`; the root `README.md`
  agrees.

---

## The authorization package and the sample product

### What it does

`AuthService.Authorization` is the NuGet package a product API installs so it does not write its own token checks. It checks a product
token **locally**: signature against the Auth Service's published keys, issuer, the product's own audience, lifetime, and algorithm. It
then lets each endpoint ask for a permission at a level, and gives the endpoint the tenant from the token. It never calls the Auth Service
per request.

Rules it enforces:

- Signature: ES256 only, with the keys from the JWKS. No `alg: none`, no HMAC (tests `TOK03_AnUnsignedTokenWithAlgNoneIsRefused`,
  `TOK03_ATokenSignedWithAHmacSecretIsRefused`).
- Audience: required, and must equal `options.Audience` exactly. A token for another product, an identity token (no audience) and a
  service token (audience `auth-service`) are all refused with 401.
- Issuer must equal `options.Issuer`. Lifetime enforced with `ClockSkewSeconds` (30).
- **The tenant comes only from the token** (`tid`). A product must take it from `User.RequireTenantId()` and from nothing in the request.
- Permissions are checked **by level, never by role name**. A role name in the token grants nothing (test `TOK03_ARoleNameInTheTokenGrantsNothing`).
- A bad token is a clean 401 problem (`code` = `unauthorized`); a good token lacking the permission is 403 (`code` = `forbidden`). Neither
  leaks exception text.
- The product refuses to start with a missing audience, issuer or JWKS URL, or a non-https JWKS URL (unless `AllowInsecureJwksUrl`).
- **`AddAuthServiceProduct` (package 1.1.0, ONB-03)** is the other way to install it: the product gives only `Url` (the Auth Service's address) and
  `ClientSecret` (the combined credential, or a bare secret with `ClientId`), and `ProductConnection` learns the issuer from
  `/.well-known/auth-service-configuration` and the product's own audience from `GET /api/auth/applications/me` with its service token, when the
  first token arrives, keeping them for `ConfigurationCacheMinutes` (60). The keys address is built from `Url`, never taken from the answer, so a
  wrong answer cannot redirect the key fetches or the secret. The judging is the same shared code (`BearerSetup`); only the issuer and
  audience come from what was learned. With nothing learned yet every token is a plain 401 and another attempt waits `MinimumRefreshSeconds`.
  It refuses to start without `Url`, with a non-https `Url` (unless `AllowInsecureUrl`) or without `ClientSecret`. It also registers
  `IAuthServiceClient` (`SubscribeTenantAsync`, `GetProductAsync`, `GetServiceTokenAsync`), which keeps the service token and asks for a
  new one once if the Auth Service turns it down. Use it or `AddAuthServiceAuthorization`, not both.
- `EndpointPolicyAudit` finds endpoints a team forgot to protect.

### How it is implemented

Package files (`src/AuthService.Authorization`):

| File | Role |
|---|---|
| `AuthServiceProductExtensions.cs`, `AuthServiceProductOptions.cs` | `AddAuthServiceProduct(Action<AuthServiceProductOptions>)` and its settings (`Url`, `ClientSecret`, `ClientId`, `AllowInsecureUrl`, `ConfigurationCacheMinutes`, `KeyCacheMinutes`, `MinimumRefreshSeconds`, `ClockSkewSeconds`); the HTTP client `AuthService.Product` |
| `ProductConnection.cs`, `ProductKeysConfigurationManager.cs` | What the product learns from the Auth Service, the service token it holds, and the key keeper that waits for it |
| `IAuthServiceClient.cs`, `AuthServiceClient.cs` | `IAuthServiceClient`, `AuthServiceProduct`, `AuthServiceSubscription`, `AuthServiceException` and their implementation |
| `BearerSetup.cs` | The token checks and the 401 and 403 answers both ways of installing share |
| `ServiceCollectionExtensions.cs` | `AddAuthServiceAuthorization(Action<AuthServiceAuthorizationOptions>)`: validates options (`ValidateOnStart`), registers the JWKS HTTP client `AuthService.Jwks` (10 second timeout), the key manager, the JWT bearer settings and the two handlers |
| `AuthServiceAuthorizationOptions.cs` | Settings (below) |
| `JwksConfigurationManager.cs` | Keeps the keys: fetch once, refresh on a timer, refresh on an unknown `kid`, keep the last good set, back off on failure. Contains `JwksDocumentRetriever` (response limit 256 KiB) |
| `EndpointExtensions.cs` | `RequirePermission(permission, level)` and `RequireTenant()` |
| `Requirements.cs` | `TenantRequirement`, `PermissionRequirement`, `TenantHandler`, `PermissionHandler` |
| `AuthServiceClaims.cs` | `GetTenantId`, `RequireTenantId`, `GetUserId`, `GetLevel`, `HasPermission` |
| `AccessLevel.cs` | `None`, `ReadOnly`, `CreateRead`, `Full` (the package has its own copy of the enum) |
| `EndpointPolicyAudit.cs` | `FindEndpointsWithoutPermissionPolicyAsync` |

Request flow in a product:

1. `UseAuthentication` runs the JWT bearer handler with `MapInboundClaims = false`, `ConfigurationManager = JwksConfigurationManager`,
   `RefreshOnIssuerKeyNotFound = true`, `SaveToken = false`. It validates signature (`ValidAlgorithms = ES256`), issuer, audience
   (`ValidateAudience`, `RequireAudience`), expiry (`RequireExpirationTime`). Lifetime is judged on the product's `TimeProvider` if it
   registers one, else the system clock (so a test can move time).
2. `UseAuthorization` runs the endpoint's policy from `RequirePermission` / `RequireTenant`: authenticated user, `TenantRequirement`
   (`TenantHandler`: `tid` is a non-empty GUID **and** `sub` is a GUID), and for `RequirePermission` also `PermissionRequirement`.
3. `PermissionHandler` passes when the token has the permission at the required level or higher; `RequirePermission` refuses a minimum
   level of `None` at startup (`ArgumentOutOfRangeException`).
4. `AuthServiceClaims.GetLevel` reads every `permissions` claim, splits at the **last** `:`, requires an exact-case level name (a number
   or other casing is ignored), and takes the highest level if a permission is listed more than once. `GetTenantId` / `GetUserId` parse
   the claims and return null if malformed.
5. Failures: `OnChallenge` writes 401 `unauthorized`; `OnForbidden` writes 403 `forbidden`, both as `application/problem+json` with no
   detail on why.

Key caching and refresh (`JwksConfigurationManager`):

- Wraps `ConfigurationManager<JsonWebKeySet>`: `AutomaticRefreshInterval` = `KeyCacheMinutes` (min 1 minute); `RefreshInterval` =
  `MinimumRefreshSeconds`.
- A token whose `kid` is not in the cached set triggers a refresh, at most once per `MinimumRefreshSeconds`, which is how a rotation is
  picked up with no restart and how a revoked key is dropped.
- If a refresh fails after a good fetch, the last good set stays in use (test `TOK03_ProductsKeepWorkingWhenTheAuthServiceIsUnreachableAfterTheFirstFetch`).
- If the **first** fetch fails, the manager returns an empty key set: every token is a 401, and it will not try again for
  `MinimumRefreshSeconds`, so a down Auth Service is not hammered (test `TOK03_WhenTheKeysCannotBeFetchedAtAllEveryTokenIsRefusedNotAccepted`).
  It recovers on its own once the JWKS can be fetched.
- Concurrent first requests share one fetch (test `TOK03_ConcurrentFirstRequestsShareOneKeyFetch`); 25 requests make one fetch.

Endpoint policy audit: `EndpointPolicyAudit.FindEndpointsWithoutPermissionPolicyAsync(services, isExempt, requirePermission = true)`
lists every `RouteEndpoint` as `METHOD /route` that is anonymous, has no authorization, or only needs a valid token; endpoints that only
`RequireTenant` pass only if `requirePermission` is false. A product writes a test that builds the app and asserts the list is empty
(health check named exempt).

The sample product (`samples/SampleProduct/Program.cs`) installs the package as a **package reference**, not a project reference
(`samples/SampleProduct/SampleProduct.csproj`; version from `samples/Directory.Packages.props`). It binds options from the `AuthService`
section of `samples/SampleProduct/appsettings.json` (`Issuer` = `AuthService`, `Audience` = `sample-product-api`, `JwksUrl`). Routes:

| Route | Rule |
|---|---|
| `GET /campaigns` | `RequirePermission("campaign.read", AccessLevel.ReadOnly)`; returns the token's `tenantId` |
| `POST /campaigns` | `RequirePermission("campaign.create", AccessLevel.CreateRead)` |
| `GET /me` | `RequireTenant()` |
| `GET /health` | `AllowAnonymous()` |

`samples/SampleProduct.Tests/SampleProductTests.cs` makes tokens with a test key and serves the matching JWKS from a stub handler, so no
Auth Service runs. It checks: the installed library is type `package` (not `project`) at the expected version; the tenant is the token's,
not the query string's; level rules (CreateRead and Full pass, ReadOnly fails the create); 403 without the permission; 401 for another
audience, expired token, forged key with a copied `kid`, no token and garbage (no "Exception" in the body); a token with no tenant
cannot reach `/me`; and the endpoint audit finds nothing besides `/health` and `/me`. The integration tests (`tests/AuthService.IntegrationTests/Tok03`)
use a similar tiny product built in the test process (`tests/AuthService.IntegrationTests/Infrastructure/SampleProductApi.cs`, which
uses the project reference, not the sample) that fetches keys from the real Auth Service, counts the fetches, can be told to fail them,
and uses `MinimumRefreshSeconds = 1` so refresh tests are quick.

Packaging and release (decision 0009): version comes only from a tag `authorization-v<semver>` on `main`; the workflow
`.github/workflows/publish-authorization-package.yml` runs the whole test suite, packs, pushes to the repository's GitHub Packages feed
and then installs the published package into the sample product and runs its tests. The claim contract (`sub`, `tid`, `aud`,
`permissions` as `key:Level`) and the public API are the versioned surface. `.github/workflows/ci.yml` also runs
`dotnet test samples/SampleProduct.Tests` on every build.

### Configuration and environment

Set by the **product**, not the Auth Service. The names below are options of the package; the section name the product binds them from
is the product's choice (the sample uses `AuthService`, giving environment names like `AuthService__Audience`).

| Option (`AuthServiceAuthorizationOptions`) | Default | What changes if it differs |
|---|---|---|
| `Audience` (**required**) | empty | Must equal the product's registered `applications.jwt_audience`. Otherwise every token is 401. Empty stops startup ("Audience is required"). |
| `Issuer` (**required**) | empty | Must equal the Auth Service's `Auth:Tokens:Issuer` (`AuthService` by default). Otherwise every token is 401. |
| `JwksUrl` (**required**) | none | Absolute URL of `/.well-known/jwks.json`. Must be https unless `AllowInsecureJwksUrl`. |
| `AllowInsecureJwksUrl` | false | Local development only. |
| `KeyCacheMinutes` | 10 | How long keys are kept before refetch. |
| `MinimumRefreshSeconds` | 30 | Shortest gap between forced fetches (unknown `kid`) and the back-off after a failed first fetch. |
| `ClockSkewSeconds` | 30 | Tolerance for token times. |
| `AuthServiceAuthorizationOptions.JwksHttpClientName` = `AuthService.Jwks` | n/a | Name of the `IHttpClientFactory` client; a product can set a proxy or handler on it. |

No secret is needed by the package. The product needs a GitHub token with `read:packages` to restore it from the feed
(package `README.md`, decision 0009).

### Failure modes and production debugging

What the product's caller sees: 401 `unauthorized` ("A valid access token is required.") for anything wrong with the token; 403 `forbidden`
("The access token does not allow this.") for a valid token without the permission or tenant.

What is logged: the package logs nothing itself. The standard JWT bearer handler logs the validation exception in the product at its
configured level (library behaviour, not repository code); the response body deliberately says nothing (test
`TOK03_RefusalsAreProblemJsonWithNoStackTraceOrExceptionText`). Raise `Microsoft.AspNetCore.Authentication.JwtBearer` to see the reason in
the product's own logs.

| Symptom | Likely cause | How to confirm |
|---|---|---|
| Every token 401 on a fresh product | `Issuer` or `Audience` wrong, or the keys could not be fetched | Decode a token: `iss`, `aud`. `curl` the `JwksUrl` from the product host. |
| Every token 401 after the product started while the Auth Service was down | First fetch failed; it retries every `MinimumRefreshSeconds` | Wait or restart; check network from the product to `JwksUrl` |
| Valid tokens 401 "Audience" | Token is for another product, or `Audience` differs by case or whitespace (exact comparison) | Compare `aud` in the token with `applications.jwt_audience` |
| 401 only some of the time right after a rotation | Product still has the old key set and sees a new `kid` | Resolves within `MinimumRefreshSeconds` of the first new-key token |
| 403 on a valid token | The `permissions` claim lacks the key at the level | Decode the token; compare with the endpoint's `RequirePermission` |
| 401 from a product for a token that works at the Auth Service console | It is an identity or tenant-admin token (`aud` missing or `auth-service-tenant`), not this product's token | Decode `aud` |
| Product starts with an options validation error | Missing audience, issuer or JWKS URL, or an `http` URL without `AllowInsecureJwksUrl` | The exception text names the option |
| Time-related 401 | Product clock off by more than `ClockSkewSeconds` | Compare clocks; `nbf`/`exp` in the token |

### Decisions and open questions

- Local validation means no per-request dependency on the Auth Service (test `TOK03_ManyRequestsFetchTheKeysOnlyOnce`) at the cost of
  up-to-15-minute staleness of permissions and no early revocation.
- Audience is checked in the package (`RequireAudience`), so a token cannot be replayed against another product.
- The package carries its own `AccessLevel` enum and claim names; it does not reference the Auth Service project, so the claim
  contract is the only coupling. A rename of `tid`, `sub`, `aud` or the `key:Level` format is a breaking change (new major version).
- Package validation against the previous version is not enabled (decision 0009, "Alternatives considered"), so a breaking public API
  change is caught only in review.
- From 1.0.2 the package is MIT licensed and also pushed to nuget.org (decision 0010). Versions 1.0.0 (earlier restrictive licence text)
  and 1.0.1 exist only on GitHub Packages: their releases stopped at the verify job, before the nuget.org step.
- **Unknown:** the currently released package version. The project file has no fixed version (it comes from the tag). Check the
  GitHub Packages feed and nuget.org.
- The package reads only the `Authorization` header (the JWT bearer default). It has no helper for the form-post hand-off (next section).

---

## The entitlements package

### What it does

ENT-07 (epic AUTH-277, decision 0016). `src/AuthService.Entitlements` is the package a product installs to know each organisation's plan and
limits; `src/AuthService.Entitlements.Testing` is its test kit. It builds on `IAuthServiceClient.SendAsync` (new in the authorization package,
1.2.0), so the product's address, secret and cached service token are shared.

- `AddAuthServiceEntitlements` registers `IEntitlements`, `IEntitlementsClient`, `AuthWebhookVerifier`, `SeenEvents` and, unless one exists, an
  `InMemoryEntitlementStore`. Options `AuthServiceEntitlementsOptions`: `WebhookSecrets` (required, validated at start), `SignatureTolerance` (5 min),
  `LimitCacheDuration` (10 min), `DuplicateWindow` (24 h), `MaxBodyBytes` (256 KB), `OnEvent`.
- `MapAuthServiceWebhook(pattern)`: body over `MaxBodyBytes` -> `413`; `AuthWebhookVerifier.Verify` fails -> `401`; `AuthWebhookParser.Parse` fails
  (not an event, missing field, `schemaVersion` above `SupportedSchemaVersion`) -> `400`; an event id already handled -> `204` with nothing done;
  otherwise the store is updated (`EntitlementsChangedEvent` and `ApplicationSubscribedEvent` apply their snapshot, `ApplicationUnsubscribedEvent` sets
  the status), `OnEvent` runs, and the answer is `204`. An exception forgets the id and answers `500`, so the Auth Service retries.
- `AuthWebhookVerifier`: HMAC-SHA256 of `{timestamp}.{body}`, lowercase hex, `v1=` entries comma-separated, compared in fixed time; the signed time
  must be within the tolerance either way. `Sign` builds the header for tests.
- `InMemoryEntitlementStore.ApplyAsync` keeps a snapshot only when its version is higher; an event (no status) keeps the known status.
- `IEntitlementsClient`: `GetLimitsAsync`, `GetPlansAsync` (`/api/auth/applications/me/limits`, `/plans`), `GetTenantAsync` (null on `404`),
  `GetChangesAsync` (`/api/auth/applications/me/entitlements`), `CatchUpAsync` (every page, returns the change number to keep).
- `IEntitlements.GetLimitAsync`: store, else one read; a key missing from the snapshot is the limit's default (limits cached, read again once
  for an unknown key); an unknown key is `KeyNotFoundException`, a tenant that never subscribed `InvalidOperationException`.
- Versions: `EntitlementsVersion` is the packages' own; `Version` is the authorization dependency's (`MinimumAuthorizationVersion`, 1.2.0).
  Released by `publish-entitlements-package.yml` from an `entitlements-v*` tag, after the matching `authorization-v*` release.

### Failure modes and production debugging

- *Every webhook is 401:* the secret in `WebhookSecrets` is not the current one (after a rotation, add the new one), or the product's clock is
  more than five minutes off.
- *Webhooks are 400 "schema version 2":* the Auth Service sends a newer event shape; update the package.
- *A product on several instances applies an event twice:* duplicates are dropped per process; versions keep the store right.

---

## The hand-off into a product

### What it does

When a person picks a product in the console, the console gets them a product token and gives it to the product, which lives at its own
address. This section covers what the API and the token do. The browser side (the picker page, the anti-forgery check, the
auto-submitting page and its security headers) is in [06-web-console.md](06-web-console.md). The decision is
[0007](../decisions/0007-hand-the-token-to-a-product-with-a-form-post.md).

Rules (decision 0007 and the code):

- The token is posted in a form **body** to the product's launch address, never put in a URL, so it is not in history, logs or referrers.
- The launch address comes from the API's answer for that person, never from the request, and is set only by a platform admin.
- The product must be `Available` to this person (Active membership, Active subscription, seat) and have a launch address, otherwise no
  exchange is made.
- A fresh exchange is made each time; nothing is cached.
- The form fields are `access_token`, `token_type` and `expires_in` (`application/x-www-form-urlencoded`).
- The launch address must be https; plain http is accepted only for localhost (`LaunchUrlValidator`).

### How it is implemented

| Step | Where | What |
|---|---|---|
| 1 | `GET /api/auth/users/me/applications?tenantId=` (identity token), `MyApplicationsEndpoints` | Same membership and suspension rules as the exchange (`tenant_access_denied`, `tenant_suspended`); per product returns `state` (NotSubscribed, SubscriptionSuspended, SubscriptionCancelled, NoAccess, Available) and `launchUrl` **only when Available** |
| 2 | `POST /api/auth/token/exchange` | The ordinary exchange (section 2), with the person's identity token |
| 3 | Console `POST /choose-product/open` (`src/AuthService.Web/Security/ProductEndpoints.cs`) | Builds a page with a form posting `access_token`, `token_type`, `expires_in` to the launch address |
| 4 | The product's launch endpoint | Receives the form post, validates the token, starts its own session, answers 303 to its own page |

Launch address administration: `PUT /api/auth/admin/applications/{applicationId}/launch-url` (platform admin), body `{ "url": "..." }`
(empty clears it), validated by `LaunchUrlValidator` (at most 2000 characters, absolute, https or `http` for loopback only, no user name or
password, no `#` fragment), stored in `applications.launch_url` (max 2000), audit `auth.application.launch_url_set` (detail: the
product key).

A product's launch endpoint (step 4) must, per decision 0007: accept a `POST` of `application/x-www-form-urlencoded` with those three
fields, check the token (signature against the published keys, audience, expiry), start its own session, and respond with 303.
**The package validates the `Authorization` header only**; the repository has neither a form-post receiver in the package nor one in the
sample product. A product must therefore validate the posted token itself, for example with `JsonWebTokenHandler` against the JWKS and its
own audience and issuer, or place the token in a header and reuse the package. See "Unknown" below.

Data: `applications.launch_url`; nothing else is written. The exchange is not audited (section 2).

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `applications.launch_url` (set per product by a platform admin, not a setting file) | null | `Application.LaunchUrl` | Null: the product cannot be opened from the picker. |
| `Auth:Tokens:ProductTokenMinutes` | 15 | section 2 | Life of the token handed over, and `expires_in`. |
| `Auth:TokenExchange:UserLimit` | 30 | section 2 | Opening products counts against the exchange budget; the console shows a wait if 429. |

### Failure modes and production debugging

- The console redirects back to the picker with an error: `not_available` (product not Available, no launch URL, or exchange 403 other than
  suspended), `tenant_suspended`, `rate_limited` (with a wait), `expired` (identity token no longer valid, 401), `unavailable` (anything
  else, such as the API being down). Mapping in `ProductEndpoints.Code`.
- Product-side failures after the post: the product answers 401 or 405. Likely causes: its launch endpoint does not accept `POST` or does
  not read the form body; `Audience`/`Issuer` mismatch; the product host's clock; the JWKS not reachable (section 5).
- A browser that blocks the post: the console's page sends a Content-Security-Policy `form-action` for the launch origin only; a launch
  address that changed after the page was built, or a redirect to another origin on the product, is the console's concern (part 6).
- Check the launch URL and standing:

```sql
SELECT key, is_active, jwt_audience, launch_url FROM applications WHERE id = '<application>';
SELECT occurred_at, detail FROM audit_entries WHERE action = 'auth.application.launch_url_set' ORDER BY occurred_at DESC LIMIT 10;
```

### Decisions and open questions

- Form post chosen over query string, URL fragment and a one-time code (decision 0007). A one-time code redeemed with the product's own
  credentials is named as a possible next step.
- The token passes through the person's browser for an instant; it is a 15-minute token for one product and tenant.
- **Unknown:** how a real product is expected to receive the form post using the package. No code in `src/AuthService.Authorization` or
  `samples/` does it. The owner should confirm whether a helper or a documented recipe is wanted.
- **Assumption (confirm):** products are expected to start their own session after the post (as decision 0007 says) rather than keep
  using the posted token on every request. The sample has no session code.

---

## Tenant isolation and the always-run isolation tests

### What it does

The guarantee is that one organisation can never read or change another's data through the Auth Service, and that a token for one
product (or for the console) never works at another. This matters because the platform serves many organisations from one database.

What is guaranteed, and the mechanism behind each:

| Guarantee | Mechanism |
|---|---|
| A tenant-scoped request acts on the token's tenant only | Tenant-admin endpoints use `TenantContext`/`RequireTenantPermission`; handlers read the tenant via `GetTenantId()` from `tid`; no tenant-scoped route has a `{tenantId}` value (the exception is the routes a product calls with its service token) |
| A request cannot name another tenant in its path, query, header or body | Those values are never read for tenant selection |
| Another tenant's record is not confirmed to exist | Endpoints answer 404, not 403, for ids from another tenant (for example `ApiKeyEndpoints.KeyNotFound`) |
| A token for product X is refused by product Y | Product audience check in the package (`aud`), and unique audiences per product |
| A tenant-admin token does not work at a product, nor a product token on the console | `aud=auth-service-tenant` versus the product's audience |
| Being Owner of tenant A gives nothing in tenant B | Permissions are computed per membership at exchange |
| A role change takes effect at the next token; the old token lasts at most 15 minutes | Short product-token life, no early revocation |
| A grant row cannot point at another tenant's parent | Composite foreign keys such as (`tenant_id`, `api_key_id`) -> `api_keys` (`tenant_id`, `id`) |
| A platform admin has no tenant access by being an admin | Exchange requires an Active membership |

### How it is implemented

The isolation tests are in `tests/AuthService.IntegrationTests/Tok04`. They run in the normal suite on every build
(`.github/workflows/ci.yml` runs `dotnet test`; the package release workflow and the image workflow run the full suite too), against a
real PostgreSQL through Testcontainers. There is no category or trait that skips them.

`IsolationTestBase` builds two tenants (A and B), two products and the people through the real API, so the tokens under test are the ones a
product would really receive. The product-side checks use `SampleProductApi` (the in-test product described in section 5).

| Test class | What is proven |
|---|---|
| `TenantIsolationTests` | A's owner token reads none of B's data on roles, members, subscriptions, API keys, audit and profile (`TOK04_TenantAsTokenReadsNoneOfTenantBsData`), while B's own reads do show it (`..._SoTheAbsenceAboveIsMeaningful`); every attempt to rename/delete B's role, disable B's member, assign roles or seats, read/revoke/rotate B's key is 404 and B's data is unchanged; a path, query, header or body naming B is served from A's token; product X's token fails at product Y (and vice versa) with 401; a product token fails on the console endpoints; a tenant-admin token fails at a product; a person in two tenants cannot use one token for the other; a platform admin gets 403 on exchange and tenant endpoints; A's audit log has only A's entries |
| `RoleDowngradeTests` | With a shared fake clock: a downgraded role gives a lower-level next token; the old token works until its 15 minutes end, then 401; removing the role leaves the next token with no permissions; revoking a seat blocks new exchanges while the old token lives on |
| `EndpointInventoryTests` | Reads the real endpoint table and classifies every endpoint (Anonymous, Identity, Service, PlatformAdmin, TenantContext, TenantPermission, Unprotected). Fails if: the anonymous set differs from the reviewed list of 15; any endpoint is unprotected; a tenant-scoped endpoint (`/api/auth/tenants/me*`, `/api/auth/tenant/me`, `/api/auth/roles*`, `/api/auth/apikeys*` except `validate`) is not tenant-admin protected or has a `{tenantId` route value; a tenant-admin write needs only a tenant context and no permission; any route with `{tenantId` is not a service-token endpoint; or anything under `/api/auth/admin/` is not platform-admin |

The reviewed anonymous list is the array `AnonymousEndpoints` in `EndpointInventoryTests`: `GET /`, `GET /.well-known/auth-service-configuration`, `GET /.well-known/jwks.json`,
`GET /health/live`, `GET /health/ready`, `GET /metrics`, `POST /api/auth/email/verify`, `POST /api/auth/email/verify/resend`,
`POST /api/auth/invitations/accept`, `POST /api/auth/login`, `POST /api/auth/logout`, `POST /api/auth/password/forgot`,
`POST /api/auth/password/reset`, `POST /api/auth/refresh`, `POST /api/auth/signup`, `POST /api/auth/token`. Adding an anonymous endpoint
means adding a line there in review.

Related suites: `Tok01` (exchange rules and rate limit), `Tok02` (JWKS and rotation), `Tok03` (the package), `Tok05` (suspended tenant),
`Key01` (API keys).

### Configuration and environment

None. The tests need Docker for Testcontainers (see the root `README.md`, "Running the tests").

### Failure modes and production debugging

- A failing `EndpointInventoryTests` after adding an endpoint means the endpoint has no explicit rule, or is anonymous and not on the list,
  or takes a tenant from the route. Fix the endpoint; do not add to the list unless it really must be open.
- A failing `TenantIsolationTests` is a security regression: treat as release-blocking.
- In production, a suspected cross-tenant leak is investigated by: the caller's `tid` (decode the token), the tenant id on the affected
  rows (`tenant_id` columns), and the audit table for the tenant (`audit_entries.tenant_id`).

### Decisions and open questions

- **Limit of what is proven:** the tests cover the Auth Service's own endpoints and the package's checks. They cannot prove a product's
  own data access filters by `RequireTenantId()`; that is the product's job. The sample demonstrates the pattern only.
- There is **no global query filter and no PostgreSQL row-level security** in the code (searched for `HasQueryFilter` and
  `ROW LEVEL SECURITY`: none). Isolation depends on every query adding the tenant condition by hand; the inventory and isolation tests are
  the safety net. A new query that forgets the tenant would be caught only if a test covers that endpoint.
- The inventory's `IsTenantScoped` uses a hard-coded list of path prefixes; a new tenant-scoped area under a different prefix would not
  be classified as tenant-scoped by that test.
- 403 versus 404: foreign ids give 404 on tenant records, but a missing permission gives 403, so a caller can tell "exists in my tenant
  but I cannot" from "not in my tenant" only for their own tenant's records, which is intended.

---

## Symptom to where to look (this part)

| Symptom a user or on-call engineer reports | Likely cause | First place to look | What to check |
|---|---|---|---|
| A product rejects every token as 401 | `Issuer` or `Audience` option wrong, or the JWKS cannot be fetched | Product's `AuthServiceAuthorizationOptions`; `JwksConfigurationManager` | Decode a token and compare `iss`, `aud`; `curl` the `JwksUrl` from the product host; check `/.well-known/jwks.json` returns keys |
| Tokens fail for a short time right after a key rotation | Product still holds the old key set and meets a new `kid` | `JwksConfigurationManager` (`MinimumRefreshSeconds`); `signing_keys` | `SELECT key_id, publish_from, sign_from, sign_until, publish_until FROM signing_keys`; the new key should have been published 24 hours earlier |
| Everyone's tokens fail after a restart or restore | Database replaced, so keys are new; or instances differ in `KeyEncryptionKey`/database | `SigningKeyService.RefreshAsync`; `Program.cs` start-up; `signing_keys` | `auth.signing_key.created` with detail `initial` in `audit_entries`; same database and secret on all instances |
| Auth Service will not start, "KeyEncryptionKey is not configured" or "must be 32 bytes" | Secret missing or wrong shape | `KeyEncryption` constructor; `Auth__Tokens__KeyEncryptionKey` (`AUTH_KEY_ENCRYPTION_KEY` in `deploy/.env`) | `openssl rand -base64 32` form; same value as before |
| Auth Service starts then fails on keys with a cryptographic error | Wrong `KeyEncryptionKey` for the stored keys | `SigningKeyService.CreatePrivateKey` | Restore the original secret; stored keys cannot be re-encrypted by the app |
| `GET /health/ready` is 503 with `signingKey: failed`; login 500 | No key signs now (worker not running, or inconsistent rows) | `SigningKeyService.HasCurrentKey`; `SigningKeyWorker`; `signing_keys` | Log "Not ready: there is no signing key that signs right now."; a row with `sign_from <= now()` and `sign_until` null or future must exist |
| Product says "invalid audience" (401) | Token is for another product, an identity or tenant-admin token, or `Audience` differs | Product option `Audience`; `applications.jwt_audience` | Decode the token `aud`; the product's own logs at JwtBearer level show the reason |
| Users still have old permissions after a role change | Old product token still valid up to 15 minutes; or the console reuses the token | `Auth:Tokens:ProductTokenMinutes`; token `exp` | The person must sign in again or open the product again (new exchange); confirm with the SQL in section 2 |
| A product cannot fetch keys | Network, https required, size limit, or wrong URL | `JwksDocumentRetriever`; `AllowInsecureJwksUrl`; `AuthService.Jwks` HTTP client | From the product host: `curl -v <JwksUrl>`; startup error mentions "must be https" |
| API key validation returns 401 `invalid_api_key` | Revoked, expired (grace ended), unknown, wrong product, tenant suspended, malformed | `ApiKeyValidationService.ValidateAsync`; `api_keys` | `SELECT * FROM api_keys WHERE key_id = '<id>'` (`revoked_at`, `expires_at`, `application_id`); `tenants.status` |
| API key validation returns 403 `subscription_not_active` or `outside_key_scope` | Subscription Suspended/Cancelled, or permission/level outside the key | `ApiKeyValidationService`; `api_key_permissions` | `subscriptions.status`; the key's grants; the `level` the product asked for |
| Creating a key returns 403 `scope_exceeds_permissions` | The creator holds less than they ask | `ApiKeyService.CreateAsync`; `PermissionResolver` | The detail names the permissions; check the creator's roles |
| Rotating a key returns 409 `api_key_not_active` | Key revoked or already expired | `ApiKeyService.RotateAsync` | `revoked_at`, `expires_at`; create a new key |
| Exchange returns 403 `exchange_denied` for an organisation that should work | No Active membership, no Active subscription, no seat, or product inactive | `TokenExchangeService.ExchangeAsync` | The three SQL queries in section 2 |
| Exchange returns 403 `tenant_suspended` | Tenant status is Suspended | `tenants.status` | Reactivation through the platform admin tools |
| Exchange returns 429 `rate_limited` | More than 30 exchanges a minute by one person, or a loop in a client | `RateLimitPolicies.Resolve`; `Auth:TokenExchange:UserLimit` | `Retry-After` header; `auth_http_requests_total{route="/api/auth/token/exchange",status="429"}` |
| Exchange returns 503 `rate_limit_unavailable` | Rate-limit store unreachable (database), or migration for `rate_limit_counters` not applied | `SharedRateLimitMiddleware` | Log "The rate-limit store could not be reached..."; database health |
| Opening a product from the console shows "not available" or does nothing | No launch URL, product not Available for that person, or the product's launch endpoint rejects the post | `MyApplicationsEndpoints`; `ProductEndpoints.Code`; `applications.launch_url` | State of the product for that person; launch URL set; the product accepts `POST` form data |
| After an emergency rotation some logins fail for a minute | Instances reload keys every 60 seconds | `SigningKeyService.RotateNowAsync`; `SigningKeyRefreshSeconds` | Wait one refresh interval; check `auth.signing_key.rotated` detail `emergency` |

## Highest-risk areas in this part

1. **`Auth:Tokens:KeyEncryptionKey` (loss or mismatch).** Without the exact secret no stored signing key can be read, the service will not
   start, and there is no re-encryption tool. Review how the secret is stored and backed up separately from the database (`docs/deployment.md`).
2. **Signing-key lifecycle with no self-repair.** If no key is signing (worker down past a `sign_until`, or inconsistent rows), login and
   exchange fail and `EnsureAsync` does not fix it. Review `SigningKeyRotation.EnsureAsync` for the "no current key" case and add an alert
   on `/health/ready` and `auth_signing_key_age_seconds`.
3. **Emergency rotation across instances and products.** Instances take up to 60 seconds to agree, and products keep trusting the old key
   until their cache refreshes. The "immediately" in the documentation is true only at the instance that handled the request.
4. **Tokens cannot be revoked.** A suspension, removed seat or role downgrade reaches a product only when the token expires (15 minutes).
   Review whether that bound is acceptable for sensitive products and whether `ProductTokenMinutes` should be lower.
5. **Tenant isolation depends on hand-written tenant conditions.** There is no query filter or row-level security, so a new query that
   omits `tenant_id` is a leak. Review every new tenant-scoped endpoint against `Tok04`, and keep the inventory test's prefix list current.
6. **API key validation surface.** A key with no product validates for any product; `/apikeys/validate` has no own rate limit; products
   may cache results with no defined bound. Review the product guidance for these.

## Unknowns and assumptions to confirm

- **Unknown:** the exact body of a 403 from the Auth Service's own policies (probably empty; no `OnForbidden` handler is registered in
  `src/AuthService.Api`). Confirm by calling a tenant endpoint with an identity token.
- **Assumption (confirm):** token exchange is not audited on purpose (volume). Evidence is only the absence of an audit action.
- **Assumption (confirm):** during the up-to-60-second window after an emergency rotation, tokens signed by a lagging instance are
  rejected by an instance that has already rotated. Inferred from `SigningKeyService`; no test covers it.
- **Unknown:** whether the reverse proxy caches `/.well-known/jwks.json` (`deploy/Caddyfile` not checked for caching).
- **Unknown:** whether products cache API-key validation results, and for how long; nothing in the repository defines it.
- **Unknown:** how a product is meant to receive the form-post hand-off using the package (no helper in `src/AuthService.Authorization`
  or `samples/`).
- **Assumption (confirm):** products start their own session after the hand-off post rather than keep using the posted token per request.
- **Unknown:** the currently released version of `AuthService.Authorization` (root `README.md` says 1.1.0; the project file takes the
  version from the release tag). Check the GitHub Packages feed.
- Inconsistency to confirm: JWKS `Cache-Control: max-age=300` and the root `README.md` ("5 minutes") against the package default
  `KeyCacheMinutes` of 10.
- Inconsistency to confirm: the Auth Service's `GetTenantId` accepts an all-zero GUID; the package's does not.
