# AuthService

Shared identity, tenant-scoped RBAC and per-product permission enforcement for the multi-product SaaS platform.
This service is deployed on its own and owns its own PostgreSQL database.

## Stack

- .NET 10 (LTS), ASP.NET Core
- EF Core 10 with Npgsql, snake_case naming
- PostgreSQL 17
- Tests: xUnit v3, Testcontainers, Shouldly

## Layout

```
src/AuthService.Api/               ASP.NET Core host
src/AuthService.Infrastructure/    AuthDbContext, shared DB registration, EF Core migrations
tests/AuthService.IntegrationTests/ Real-database integration tests (Testcontainers)
```

## Prerequisites

- .NET 10 SDK (see `global.json`)
- A running Docker engine (Docker Desktop, Rancher Desktop or Podman with the Docker API)
- The `docker` CLI on your PATH (used by `TST_TC5`)

## Running the tests

```bash
dotnet tool restore
dotnet test
```

Each test run:

1. starts one throwaway `postgres:17-alpine` container. It is never reused and is removed at the end of the run, even if the run crashes.
2. applies the real EF Core migrations once to a template database (`auth_template`).
3. gives each test class its own copy of that template database. The copy is dropped when the test class finishes.

The first run pulls the Postgres image, so it takes longer.

## Writing integration tests

Derive every API test class from `AuthApiTestBase` (`tests/AuthService.IntegrationTests/Infrastructure`). Each test gets
the real service running in-process, its own migrated database copy, and an `HttpClient` (`Client`) that calls it.
Call the same HTTP endpoints as a real client, not internal service methods.

```csharp
public sealed class MyTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    [Fact]
    public async Task TEN_TC1_Something() =>
        (await Client.GetAsync("/", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
}
```

## Continuous integration

`.github/workflows/ci.yml` runs on every pull request and every push to `main`: restore, build in Release (warnings are
errors), then the whole test suite against a throwaway PostgreSQL started by Testcontainers. A failing test fails the
**Build and test** job. Failing tests are shown by name, including their Test Case ID (e.g. `TST_TC6_...`), as
annotations on the pull request and in the workflow run's summary; the TRX results are kept as the `test-results`
artifact for 14 days.

**The merge gate is a repository setting, not part of the workflow.** To make a failing test block merging, require the
`Build and test` status check on `main` (Settings → Branches → branch protection, or a ruleset) and require pull
requests. GitHub offers this on private repositories only with a paid plan (GitHub Pro, Team or Enterprise); on the Free
plan the repository must be public. Until that is enabled, a red check is advisory only.

## Configuration

| Setting | Meaning |
|---|---|
| `ConnectionStrings:AuthDb` | PostgreSQL connection string. Set in `appsettings.Development.json` for local runs. |
| `Auth:Tokens:KeyEncryptionKey` | The secret that encrypts signing keys before they are stored: 32 random bytes, base64 (`openssl rand -base64 32`). **Required outside Development and Testing**: the API refuses to start without it. Pass it as a secret (`Auth__Tokens__KeyEncryptionKey`), never in a committed file. **Losing it means losing every stored signing key.** |
| `Auth:Tokens:SigningKeyRotationDays` / `SigningKeyPrePublishHours` / `SigningKeyPostRetireHours` | How long a key signs (default 90 days), how long before that it is published (24 h), and how long it stays published after (24 h). |
| `Auth:Audit:RetentionDays` / `RetentionCheckSeconds` / `RetentionEnabled` | How long audit entries are kept (default 365 days), how often the cleanup job runs (default every 86,400 s, once a day, and once at startup), and whether it runs at all. |
| `Auth:Tokens:SigningKeyRefreshSeconds` | How often each instance reloads the keys and checks whether it is time to rotate. Default 60. |
| `Auth:Tokens:Issuer` | `iss` claim of issued tokens. Default `AuthService`. |
| `Auth:Tokens:AccessTokenMinutes` | Identity access token lifetime. Default 15. |
| `Auth:Tokens:RefreshTokenDays` | Refresh token lifetime. Default 30. |
| `Email:Provider` | Who sends email: `Brevo`, `File` (writes each email to a file) or `InMemory` (tests). **Required outside Development and Testing**, where it defaults to `File` and `InMemory`. |
| `Email:BrevoApiKey` | Brevo API key. **Required for `Brevo`.** A secret: pass it as `Email__BrevoApiKey`. |
| `Email:FromAddress` / `Email:FromName` | Sender of every email. `FromAddress` must be a sender verified with Brevo. |
| `Email:PublicBaseUrl` | Address that links in emails point at (the page that handles `/verify-email`, `/reset-password`, `/accept-invitation`). |
| `Email:FileDirectory` | Where the `File` provider writes emails. Default `artifacts/emails` under the API project. |
| `Email:MaxAttempts` / `Email:RetryDelaySeconds` | How often a failing email is tried (default 5 attempts, waiting 30 s, 2 min, 10 min, 1 h between them). |
| `Email:DispatchIntervalMilliseconds` / `Email:DispatcherEnabled` | How often the background worker looks for emails to send, and whether it runs at all. |
| `Auth:LoginProtection:MaxFailedAttempts` / `LockoutMinutes` | Wrong passwords in a row after which an account is locked, and for how long. Default 5 and 15. |
| `Auth:Tokens:ServiceTokenMinutes` | How long a product's service token is valid. Default 10. |
| `Auth:Tokens:ProductTokenMinutes` | How long a product token (token exchange) is valid. Default 15. It bounds how stale the permissions inside can get. |
| `Auth:Applications:SecretGraceHours` | How long a product's old client secret keeps working after a rotation. Default 24. |
| `Auth:Applications:TokenIpLimit` / `TokenIpWindowMinutes` | How many service-token requests one IP may make per window, across all products, before getting 429. Default 60 per minute. |
| `Auth:LoginProtection:IpLimit` / `IpWindowMinutes` | How many login requests one IP may make per window, across all accounts, before getting 429. Default 20 per minute. |
| `Auth:EmailVerification:TokenHours` | How long a verification link works. Default 24. |
| `Auth:EmailVerification:ResendCooldownSeconds` | An account sent a link less than this long ago is not sent another. Default 60. |
| `Auth:EmailVerification:ResendIpLimit` / `ResendIpWindowMinutes` | How many resend requests one IP may make per window before getting 429. Default 10 per 10 minutes. |
| `Auth:PasswordReset:TokenMinutes` | How long a password reset link works. Default 60. |
| `Auth:PasswordReset:RequestCooldownSeconds` | An account sent a reset link less than this long ago is not sent another. Default 60. |
| `Auth:PasswordReset:IpLimit` / `IpWindowMinutes` | How many forgot or reset requests one IP may make per window before getting 429. Default 10 per 10 minutes. |

### Signing keys and the JWKS

Tokens are signed (ES256) with keys the service creates and keeps itself, in the `signing_keys` table, so every instance agrees
on them. The private half is encrypted (AES-256-GCM) with `Auth:Tokens:KeyEncryptionKey` and is never stored or logged in plain
text. In Development and Testing a fixed, publicly known encryption key is used, so a development database survives restarts;
it is worthless for anything real, which is why production must set its own.

The public keys are published, with no authentication, at **`GET /.well-known/jwks.json`**, so product APIs can check a token's
signature locally. The `kid` in a token's header names the key that signed it.

Keys rotate on a schedule, and the timing is chosen so no token is ever signed by a key a product has not been told about, and no
token already issued suddenly stops validating:

1. A new key is **published 24 hours before it starts signing**. Every instance reloads the keys about once a minute, so all of
   them, and every product that caches the JWKS (5 minutes), know the key long before a token carries it.
2. Every **90 days** the new key takes over signing.
3. The previous key **stays published for 24 hours after** it stops signing, so tokens issued just before the switch still validate
   (identity tokens last 15 minutes).

If rotation is overdue (the service was down), the next key still waits the full 24 hours before signing. An **emergency
rotation** replaces the key immediately and removes the old one from the JWKS at once, invalidating the tokens it signed. It
is triggered by a platform admin with `POST /api/auth/admin/signing-keys/rotate` (see "Platform admins" below).

Changing `KeyEncryptionKey` makes the stored keys unreadable. Re-encrypting them under a new secret is not built yet.

### Email

Email is never sent inside a request. A feature queues it in the `email_outbox` table, in the same transaction as the
change that caused it, and a background worker sends it through the configured provider. A failing email is retried with
growing delays and logged; after the last attempt it is marked `Failed`. Switching provider is a configuration change only.

Logs mention an email's id, template and attempt number, never its address, token or link. The `email_outbox` row keeps the
token only until the email is sent or given up on. In Development, open the files the `File` provider writes to follow a
verification link.

### Login protection

Login is protected against password guessing in two ways:

- **Per account:** each wrong password is counted. The fifth in a row locks the account for 15 minutes. A locked account is
  refused even with the right password, with exactly the same 401 as a wrong password, so an attacker learns nothing. Attempts
  during a lock neither count nor extend it, and a right password clears the count. The check costs the same time whether or
  not the account is locked or exists.
- **Per IP address:** one address may make a limited number of login requests per minute across all accounts, then gets 429
  with `Retry-After`. Behind a reverse proxy this sees the proxy's address until forwarded headers are configured for the
  deployment.

The lockout is logged by user id, never by email. The audit log (AUD-01) will record it too once it exists, and password
reset (TEN-10) will clear the count.

### Audit log

Security events are recorded in the `audit_entries` table so who did what, when and in which tenant can always be traced. Each
entry has **who** (user, product, API key, the system, or anonymous), **what** (a stable code such as `auth.login.succeeded`),
**when**, the **tenant** if there is one, the caller's **IP address**, and an optional short code of detail (`wrong_password`).

What is recorded today: signup, tenant creation, login success and failure (with the reason: wrong password, unknown account,
locked, disabled, email not verified), lockouts, email verification, completed password resets, refresh-token reuse, and signing-key
creation and rotation. Events from stories not built yet (role and permission changes, member and product-access changes,
invitations, API keys, subscriptions, seat limits, platform-admin actions) are recorded when those stories land. The codes live in
`AuditActions`.

- **Written with the action, not after it.** An entry is saved in the same transaction as the action it records, so it exists if
  and only if the action happened. A signup that rolls back leaves no entry.
- **No secrets, by construction.** The only free text is a short code (lowercase letters, digits, dots, underscores), so a
  password, token or address cannot be recorded by mistake. A failed login for an unregistered email records nothing about the
  email that was tried.
- **Append-only, enforced by the database.** Triggers refuse every `UPDATE`, every `TRUNCATE`, and every `DELETE` except the
  retention job's, which switches on a setting that lasts only for its own transaction. No endpoint edits or deletes entries.
  The application's own database user can still change the triggers, so for stronger protection give it `INSERT` and `SELECT`
  only on this table and run the retention job as a separate role.
- **Kept for a year.** A daily job deletes entries older than `Auth:Audit:RetentionDays` (365).
- **The IP is the caller's as the service sees it**: behind a reverse proxy, the proxy's address, until forwarded headers are
  configured for the deployment.

A platform admin reads the whole log with `GET /api/auth/admin/audit` (see "Platform admins" below). Not built yet: the tenant-scoped
read (`GET /api/auth/tenants/me/audit`), which needs tenant-scoped tokens (TOK-01) and role permissions.

### Platform admins

A few operations belong to whoever runs the platform, not to any tenant: rotating the signing keys, reading the whole audit log,
and registering products. They live under **`/api/auth/admin/*`** and need a **platform admin**: a user with the
`is_platform_admin` flag set.

- **The flag can only be set in the database.** No endpoint and no request field can change it, and no request or response type
  has a field for it. Everyone starts as `false`. To make someone an admin, run SQL against the database, for example
  `UPDATE users SET is_platform_admin = true WHERE email = 'ops@example.com';`.
- **It is checked on every request, not stored in the token.** Granting or revoking it takes effect immediately, even for a token
  already issued, and no token can carry the privilege.
- **It opens the admin area and nothing else.** An admin is an ordinary user everywhere else, and gets no access to any tenant's data.
- **The policy is applied to the whole `/api/auth/admin` group**, so an endpoint added there cannot be left open by accident. A test
  checks that every endpoint under that path requires it.
- No token gets 401. A token that is not an identity token (it carries an audience) or whose user is not an admin gets 403.
- Every admin action is audit-logged under the admin's own identity.

| Endpoint | What it does |
|---|---|
| `POST /api/auth/admin/signing-keys/rotate` | Emergency rotation: a new key signs now and the old one leaves the JWKS at once, so tokens it signed stop validating (including the caller's own). |
| `GET /api/auth/admin/audit` | The whole audit log, newest first, across all tenants. Filters: `action`, `tenantId`, `actorId`. Paging: `limit` (1 to 200, default 50), then pass the returned `next` values back as `before` and `beforeId`. Reading it is itself audited. |
| `POST /api/auth/admin/applications` | Register a product (see "Applications" below). |
| `POST /api/auth/admin/applications/{id}/permissions` | Register or update a product's permission catalog. |
| `PUT /api/auth/admin/applications/{id}/active` | Make a product available to subscribe to (`{"isActive": true}`), or withdraw it. |
| `POST /api/auth/admin/applications/{id}/secret/rotate` | Replace a product's client secret; the old one keeps working for a grace period (see "Product client credentials"). |

### Applications (products)

A **product** (for example WhatsApp SaaS) is registered by a platform admin with `POST /api/auth/admin/applications`, sending a
`key`, a display `name` and a `jwtAudience`.

- **`key`** is a short stable name: lowercase letters, digits and hyphens, up to 63 characters, starting and ending with a letter
  or digit. It is trimmed and lowercased, and unique across the platform (a duplicate gets **409** `application_key_taken`).
- **`jwtAudience`** is the `aud` value its tokens will carry. It is unique too (**409** `audience_taken`), so no two products can
  ever accept each other's tokens.
- A new product is **inactive** until its permission catalog is registered (APP-02): a tenant cannot subscribe to a product that has
  nothing to grant.
- Registration also issues the product's **client ID** (`cl_...`) and **client secret**. The secret is in that one response and
  **nowhere else, ever**: only its hash is stored (in its own table, so rotation can keep an old secret alive for a grace period),
  it is never logged or audited, and the response is sent with `Cache-Control: no-store`. A lost secret is replaced by rotation, not
  recovered (see "Product client credentials" below).
- Registration is audit-logged under the admin, with the product's key as the detail.

#### Permission catalog

A product owns a **catalog** of the actions it can control, such as `campaign.create`. A platform admin registers it with
`POST /api/auth/admin/applications/{id}/permissions` and a body of `{"permissions": [{"key", "label", "description"}]}` (1 to 200
entries per request).

- A **key** is 1 to 100 lowercase letters, digits and `. _ -`, trimmed and lowercased. It is unique **within its product**, not
  across the platform, so two products can both have `report.export`. A key repeated inside one request, or any invalid entry,
  rejects the whole request with **400** and registers nothing.
- The call is an upsert. A new key creates a permission (**201** when the request added any). An existing key is **updated in
  place**, keeping its id, which is what roles will refer to, so grants of it survive a relabel (**200** when nothing new was added).
  An unchanged entry is left untouched.
- Registering **never removes** a permission. A key a product stops using stays in the catalog, because a role may still grant it.
  The response lists the whole catalog after the change, with how many entries were created, updated and unchanged.
- An unknown product id gets **404** `application_not_found`. Registrations of one product are serialised with a row lock, so two
  simultaneous ones cannot insert the same key twice.
- `PUT /api/auth/admin/applications/{id}/active` with `{"isActive": true}` activates the product, but only once it has at least one
  permission (otherwise **409** `no_permissions_registered`). `false` withdraws it. Repeating either is harmless, and only real
  changes are audited.
- Registering permissions, activating and deactivating are each audit-logged under the admin, with the product's key as the detail.

### Working on a tenant (the built-in Auth Service product)

A tenant's own people (roles, members, subscriptions, invitations) are managed through endpoints under `/api/auth/tenants/me/...`
and `/api/auth/tenant/me`. These do not take a tenant from the request. They take a **tenant-admin token**: an ordinary product token
(see "Token exchange") for the **Auth Service's own product**, so the service protects itself the way every other product does.

1. Log in, then call `POST /api/auth/token/exchange` with your `tenantId` and the built-in product's fixed id,
   `00000000-0000-7000-8000-00000000a07a`.
2. Send the returned token as `Authorization: Bearer ...`. Its `aud` is `auth-service-tenant`, its `tid` is the tenant, and its
   `permissions` list what your roles allow in the Auth Service (`key:Level`, as for any product).

- **No subscription and no seat are needed.** Any Active member of a tenant can get this token; what they may do is decided by their
  roles. The built-in **Owner** role holds Full on the whole catalog.
- The tenant of every such request is the token's `tid`. A tenant id in the path, query string or body is never read, so it cannot be
  tampered with. An identity token, another product's token or a service token is refused (403).
- The product is stored by the service itself at startup (its catalog lives in code and is synced, add-only, like the signing
  keys). It is not in `GET /api/auth/applications`, has no client credentials, and the admin API treats it as not found, so it
  cannot be edited, deactivated or given a secret. Its key `auth-service` and audience `auth-service-tenant` cannot be registered by
  anyone else.
- A tenant-admin token, like any product token, is not revoked early: a disabled membership or changed role applies at the next
  exchange (within 15 minutes).

`GET /api/auth/tenant/me` (TEN-05) is the first such endpoint: it returns the profile (`id`, `name`, `status`, `createdAt`) of the
tenant in the token, and needs no permission beyond belonging to it.

### Token exchange (product tokens)

A logged-in person gets into one product, in one tenant, by trading their identity session for a **product token**:
`POST /api/auth/token/exchange` with an identity access token and a body of `{"tenantId": "...", "applicationId": "..."}`. The
reply is `{"accessToken", "tokenType": "Bearer", "expiresIn": 900}`, sent with `Cache-Control: no-store`. A product token, a
service token or no token cannot be exchanged (403 / 401), and who is asking comes only from the verified token.

It is refused with **403** `exchange_denied` unless **all** of these hold, read fresh from the database on every exchange:

1. The caller has an **Active** membership in the tenant (none, Invited and Disabled are refused) and the tenant is not suspended.
2. The tenant has an **Active subscription** to the product, and the product is active.
3. The membership has an **access grant** (a seat) for the product. Roles that grant permissions are not enough on their own.

Every refusal is the same response, so a caller cannot tell a real tenant or product from an invented one. A missing id is **400**.

**The token** lives 15 minutes (`Auth:Tokens:ProductTokenMinutes`) and is signed with the same key as every other token, so a
product validates it locally against the JWKS. It is for exactly one product and one tenant:

| Claim | Value |
|---|---|
| `aud` | the product's JWT audience (and no other) |
| `tid` | the tenant (and no other) |
| `sub` | the person |
| `permissions` | an array of `key:Level` strings for **this product only**, for example `["campaign.create:Full", "campaign.read:ReadOnly"]`; permissions at level `None` are left out |
| `iss`, `iat`, `nbf`, `exp`, `jti` | as for other tokens |

Levels are `None`, `ReadOnly`, `CreateRead` and `Full`, widest last. A person with several roles gets the **highest** level any of
them grants for each permission (there are no deny rules). The built-in **Owner** role holds `Full` on the product's whole
catalog as it is at exchange time, with no grants stored, so a permission added later is in the Owner's next token. Grants from
custom roles are merged in once roles exist (ROL).

Nothing is revoked early: a token already issued stays valid until it expires (a suspension, a revoked seat, a role change or a
logout take effect at the next exchange). That is by design; the 15-minute life bounds how stale it can get.

### Subscriptions

A tenant gets to use a product through a **subscription**. A product subscribes a tenant to itself with
`POST /api/auth/tenants/{tenantId}/subscriptions`, called with its **service token** (see "Product client credentials"), typically
from its own onboarding or checkout flow. The body is optional: `{"maxSeats": 3}` limits how many people can have access (at least
1; left out means unlimited). The product is always the caller's own, from the verified token, so a product can only subscribe
tenants to itself. A tenant's own people cannot subscribe themselves (403).

- **201** with the subscription (`status`, `maxSeats`, `seatsUsed`) for a new one. A product that is not active gets **400**
  `application_not_active`, an unknown tenant **404**, and a suspended tenant **409** `tenant_suspended`.
- **Idempotent.** Subscribing a tenant that is already Active returns **200**, creates no second row, queues no second event and
  leaves the seat limit alone (changing it is SUB-04's call). A tenant that was Suspended or Cancelled and subscribes again is
  Active again on the limit it sends, reusing its one row.
- **The Owner gets access at once**, and counts as the first seat. Other members get access later (SUB-05).
- **An `ApplicationSubscribed` event is queued for the product** in the `webhook_outbox` table, in the same transaction as the
  subscription: if the subscription fails, no event exists. Its id is the event id the product deduplicates on. Signed delivery with
  retries, and the product's callback URL, arrive with SUB-06; until then events wait in the outbox.
- Subscribing is audit-logged with the product as the actor and the tenant it concerns.

### Roles

A tenant defines its own roles, and a role grants **permissions at a level**. `POST /api/auth/roles` creates one, called with a
**tenant-admin token** that holds `roles.manage` at `Full` (the Owner role does):

```json
{ "name": "Marketer",
  "permissions": [ { "applicationId": "...", "permission": "campaign.create", "level": "CreateRead" } ] }
```

- **Levels** are `None`, `ReadOnly`, `CreateRead` and `Full` (by name; numbers are refused). A grant is a level, never a yes or no.
  `None` is stored but gives no access.
- **Name:** required, up to 100 characters, trimmed, and unique **per tenant** ignoring case (**409** `role_name_taken`), so
  "support" cannot sit beside "Support", or "owner" beside the built-in Owner. Two tenants can use the same name.
- **Permissions** may be left out (a role that only carries access). Each is a product id plus a permission key from that
  product's catalog. The key is matched ignoring case. A grant is refused with **400**, creating nothing, if:
  - the tenant has no **Active** subscription to the product (Suspended and Cancelled do not count; the Auth Service's own product
    is always allowed), or the product id does not exist: both give the same answer, so ids cannot be probed;
  - the key is not in that product's catalog (a typo would otherwise be a role that silently does nothing);
  - it repeats a product and permission already in the request, or the level is not one of the four.
- The tenant and the person come from the token. The creation is audit-logged (`auth.role.created`, with the role id).
- **What a role is for:** a member who holds roles gets, in their next token exchange, every permission those roles grant for the
  requested product, at the **highest** level any of them grants (there are no deny rules). Nothing from another product or tenant
  is in the token.

#### Listing, changing and deleting roles

All three are called with a **tenant-admin token** and act only on the token's tenant. A role of another tenant, or one that does
not exist, is always **404** (never 403), so its existence is not confirmed.

- **`GET /api/auth/roles`** (needs `roles.read`, ReadOnly) lists the tenant's roles, the built-in Owner first and then by name:
  `{id, name, isBuiltIn, memberCount, permissions, createdAt}`. The Owner lists no permissions: it holds Full on every product's
  whole catalog. `roles.read` and `roles.manage` are separate permissions; one does not imply the other.
- **`PATCH /api/auth/roles/{id}`** (needs `roles.manage`, Full) changes the name, the permissions, or both. A field you leave out
  is left alone; `permissions` replaces the **whole** set (send `[]` to clear it), with the same checks as creating a role. Only the
  difference is written, so an unchanged grant keeps its row. A rejected change (400 or 409) changes nothing. Holders see the new
  permissions at their **next token exchange**; a token already issued keeps what it was given until it expires.
- **`DELETE /api/auth/roles/{id}`** (needs `roles.manage`, Full) deletes a role and its grants, but only when **no member holds it**
  (**409** `role_in_use`; move them to other roles first). It returns **204**.
- **The built-in Owner role can never be edited, downgraded or deleted**: any PATCH or DELETE of it is **403**
  `owner_role_immutable`.
- Updates and deletions are audit-logged (`auth.role.updated`, `auth.role.deleted`, with the role id).

#### Assigning roles to a member

`PUT /api/auth/tenants/me/members/{membershipId}/roles` with `{"roleIds": ["...", "..."]}` sets the **complete** set of roles a
member of the caller's tenant holds, atomically. It needs a tenant-admin token holding `roles.assign` at `Full`. The new set applies
from the member's **next token exchange**; tokens already issued are not changed.

- **1 to 10 roles.** An empty set is **409** `roles_required` (to remove someone's access, disable the membership instead), more than
  ten is **400**, and the same role named twice counts once. The member keeps their old roles whenever the call fails.
- **404, never 403, for anything outside the caller's tenant:** a membership or a role of another tenant, or one that does not exist,
  all give the same `not_found`, so ids are not confirmed.
- **Roles never change seats.** Product access is granted separately (SUB-05).
- **The Owner role is protected.** Only an Owner can give it or take it away (**403** `owner_role_restricted`), or anyone allowed
  to assign roles could make themselves an Owner. A change that would leave the tenant with no **active** Owner is refused
  (**409** `last_owner`); make someone else an Owner first. Changes in one tenant take turns, so two admins acting at once cannot
  break this.
- Audit-logged (`auth.membership.roles_changed`, with the membership id).

#### Giving members access to a product (seats)

Having a subscription is not enough for a person to use a product: they also need **access**, which takes one **seat**. Roles only
decide what they can do once they are in. A tenant admin holding `access.manage` at `Full` (the Owner does) calls, with a
tenant-admin token:

- `PUT /api/auth/tenants/me/members/{membershipId}/applications/{applicationId}` to give access.
- `DELETE` on the same path to take it away.

- **Granting** locks the subscription row, counts the seats in use (members whose membership is **Invited or Active** and who have
  access) and returns **409** `no_seat_available` when none is free, creating nothing. Two requests for the last seat can never both
  succeed, and a change of the limit (SUB-04) takes turns with them. It returns **201** with `seatsUsed` and `maxSeats`;
  granting access a member already has is a no-op (**200**).
- The tenant must have an **Active** subscription to the product, otherwise **400** `application_not_subscribed` (an unknown
  product, an unsubscribed one, a suspended one and the Auth Service's own product all give the same answer). A **Disabled**
  membership cannot be given access (**409** `membership_disabled`).
- A membership of another tenant, or one that does not exist, is **404**, never 403.
- **Revoking** is always allowed, even on a suspended subscription, so seats can be freed. It returns **204**, also when there was
  nothing to revoke. It takes effect at the member's **next token exchange**, which is refused for a product without access.
- Changing a member's roles never changes seat usage; neither does editing what a role grants.
- Both calls are audit-logged (`auth.membership.access_granted` / `access_revoked`) with the detail `membership-id.product-key`.

#### Listing a tenant's subscriptions

A tenant admin sees the tenant's subscriptions with `GET /api/auth/tenants/me/subscriptions`, called with a **tenant-admin token**
(see "Working on a tenant") that holds the `subscriptions.read` permission (the Owner role does). The tenant is the token's `tid`:
`me` is not an id the client can change, and a `tenantId` in the query string is ignored.

Each item is `{applicationId, applicationKey, applicationName, status, maxSeats, seatsUsed, createdAt, updatedAt}`, ordered by product
name, and includes **every** status (Active, Suspended and Cancelled), so a tenant can see a subscription that was ended.

- **`seatsUsed`** counts members with access to the product whose membership is **Invited or Active**. A Disabled member keeps
  their access row, so re-enabling restores it, but uses no seat. The same rule gives `seatsUsed` in the subscribe and seat-limit
  responses. `maxSeats` is null for unlimited.
- A member without the permission gets 403; an identity, service or other product's token gets 403; no token 401. The Auth Service's
  own built-in product is not a subscription and never appears.

#### Suspending and cancelling

A product stops a tenant's access with `PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}` and a body of
`{"status": "Suspended"}` or `{"status": "Cancelled"}` (not case sensitive), called with its **service token**. Anything else is
**400**, including `Active`: a tenant becomes Active again by being subscribed again (see above), not by this call.

- **New access stops at the next token exchange** (TOK-01 returns 403 for a Suspended or Cancelled subscription). Tokens already
  issued are **not** revoked; they run out within their short life and cannot be renewed. A product that must cut access sooner
  checks the subscription itself. This is by design, not a bug.
- **Everyone's seat is kept**, so subscribing the tenant again restores all of its people at once.
- An `ApplicationUnsubscribed` event (with the new `status`) is queued for the product in the same transaction as the change.
- Setting the status a subscription already has is a no-op: 200, no second event. Suspended can become Cancelled. **Cancelled
  cannot become Suspended** (**409** `subscription_cancelled`); it ends there until the tenant is subscribed again.
- Like the seat limit, the application must be the caller's own: another product's subscription, a tenant that never subscribed and
  an unknown tenant all get the same **404** `subscription_not_found`. A tenant user, an admin or a product token gets 403.
- Audit-logged (`auth.subscription.suspended` / `cancelled`) with the product as the actor and the tenant it concerns.

#### Seat limits

A product sets how many people may use it in a tenant, when the tenant's plan changes, with
`PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}/seats` and a body of `{"maxSeats": 25}`. Billing stays in the
product; only the number crosses. Called with the product's **service token**, never a user's: a tenant user, an admin or a
product token gets 403, no token 401.

- `maxSeats` must be in the body: a whole number of at least 1, or `null` for unlimited. A missing field, `0`, a fraction, a
  string or anything else is **400**, so a mistyped field name can never silently remove a tenant's limit.
- **Lowering it below current usage is accepted** (200): people who already have access keep it, and only new grants are blocked
  until usage is back under the limit (SUB-05). The call never fails because of how many seats are in use.
- The `applicationId` in the path must be the caller's own. Another product's subscription, a tenant that never subscribed and a
  tenant that does not exist all get the same **404** `subscription_not_found`, so a product learns nothing about the others.
- The change is audit-logged with the product as the actor and the tenant it concerns. It works on a Suspended or Cancelled
  subscription too, and changes only the limit.

#### Listing products for tenants

`GET /api/auth/applications` with any signed-in person's identity token returns the products a tenant can connect, for the "connect a
new product" screen: `[{"id", "key", "name"}]`, ordered by name. Only **active** products appear, so a product still waiting for its
permission catalog, or withdrawn, is not offered. It needs no subscription and no tenant. The product's audience and client
credentials are not included. No token is 401; service and product tokens are 403.

#### Product client credentials

A product calls the Auth Service as itself with the **client ID** and **client secret** it was given at registration (OAuth 2.0
client-credentials style).

```bash
curl -X POST https://auth.example.com/api/auth/token \
  -d grant_type=client_credentials -d client_id=cl_... -d client_secret=...
```

The body can be the form above or JSON with the same three fields. The reply is `{"access_token", "token_type": "Bearer", "expires_in": 600}`
(10 minutes; ask again when it expires), sent with `Cache-Control: no-store`.

- Wrong secret, unknown client and an expired secret all get the **same 401** `invalid_client`, so the reply never says which. A
  missing field is **400** `invalid_request`, any other grant **400** `unsupported_grant_type`. Requests are rate limited per IP
  (`Auth:Applications:TokenIpLimit`, 429). Neither the client ID nor the secret is logged.
- A product that is still inactive can get a token: it has to be able to call the Auth Service before it is offered to tenants.
- A **service token** names the product (`app_id`, `client_id`, `token_use: service`) and carries the audience `auth-service`
  (reserved: no product may register it). Its `sub` is `app:<id>`, never a user id. So it is refused with **403** on every identity
  and admin endpoint, and an identity or product token is refused on the service endpoints. Which product it is comes only from
  the verified token, never from the request. `GET /api/auth/applications/me` returns the calling product's own record. The
  subscription, seat-limit and API-key endpoints of SUB and KEY use the same `ServiceToken` policy.
- A token already issued stays valid until it expires (10 minutes), even if its secret has since been rotated out.

**Rotating the secret.** A platform admin calls `POST /api/auth/admin/applications/{id}/secret/rotate`. It returns the new secret
once (never again, `no-store`) and `previousSecretsExpireAt`. The old secret keeps working until then: 24 hours by default
(`Auth:Applications:SecretGraceHours`), or `{"graceHours": n}` in the body (0 to 720). **`graceHours: 0` cuts the old secret off at
once, for a leaked secret.** Rotating again never extends an older secret's life. Unknown product is **404**. Rotation is
audit-logged under the admin with the product's key as the detail; no secret is ever recorded. Only hashes are stored.

### Calling protected endpoints

Endpoints that act as the logged-in person (for example `GET /api/auth/users/me`) take the **identity access token** that login
and refresh return, as `Authorization: Bearer <token>`. The service checks the signature (with its own signing key), the
issuer and the expiry (with 30 seconds of clock skew), and accepts only the algorithm it signs with.

- No token, a malformed one, an expired one, or one signed by another key or issuer gets **401** with code `unauthorized` and a
  `WWW-Authenticate: Bearer` header.
- A correctly signed token that carries an audience (a product token, TOK-01) is refused with **403** on identity endpoints.
- Who the caller is comes only from the token's `sub` claim, never from the path or query string, so a request cannot ask for
  someone else's data by passing an id.

### Password reset

Someone who forgot their password recovers it through an emailed single-use link:

1. `POST /api/auth/password/forgot` with an email always answers 202 with the same body, whether or not the email is
   registered, so it cannot be used to find out which addresses exist. If an account exists, a reset token (stored only as a
   hash, valid 60 minutes, single use) is saved and the email is queued, in the same transaction.
2. The link points at `Email:PublicBaseUrl` plus `/reset-password?token=...`. That page posts the token and a new password to
   `POST /api/auth/password/reset`: 200 on success, 410 for an unknown, expired or used link, 400 for a password that breaks
   the rules (8 to 128 characters), which does not use the link up.
3. A successful reset sets the new password, clears any lockout and run of wrong passwords, **signs the person out
   everywhere** (every refresh token is revoked), and retires their other outstanding reset links.

Both endpoints are limited per IP address (429), and an account is not sent a new link more often than the cooldown allows,
so a stranger cannot flood someone's inbox. A reset is logged by user id, never by email. The audit log (AUD-01) will record
it too once it exists.

### Email verification

A new account starts unverified and cannot log in (403 `email_not_verified`) until its email is verified:

1. Signup creates the account and, in the same transaction, a verification token (stored only as a hash, valid 24 hours,
   single use) and the queued email that carries it.
2. The link in the email points at `Email:PublicBaseUrl` plus `/verify-email?token=...`. That page posts the token to
   `POST /api/auth/email/verify`: 200 on success (and on an address that is already verified), 410 for an unknown,
   expired or used link.
3. `POST /api/auth/email/verify/resend` asks for a new link. It always answers 202 with the same body, whether or not the
   email is registered, so it cannot be used to find out which addresses exist. It is limited by a per-account cooldown
   and a per-IP rate limit (429).

Behind a reverse proxy, the per-IP limit sees the proxy's address until forwarded headers are configured for the
deployment.

## Adding a migration

```bash
dotnet ef migrations add <Name> \
  -p src/AuthService.Infrastructure \
  -s src/AuthService.Api \
  -o Persistence/Migrations
```

`TST_TC2` fails if the EF Core model changes without a matching migration.

## Rules

- **Migrations only; never use `EnsureCreated()`.** The test database is built from the same migrations as production.
- **The API never migrates on startup.** Migrations are applied explicitly.
- **Keep one Postgres version everywhere.** The test image is pinned in `tests/AuthService.IntegrationTests/Infrastructure/TestSettings.cs` and must match production.
- **Name tests after their Test Case ID.** Every test method name starts with its ID from the spec workbook (e.g. `TST_TC1_...`).

## History and decisions

- **Commits explain why, not what.** Start the subject with the ticket ID (`AUTH-33 ...`) and use the body for the
  problem and the approach. Enable the template once per clone: `git config commit.template .gitmessage`.
- **Pull requests** follow `.github/pull_request_template.md`.
- **Lasting decisions** (database, UI technology, team rules) are recorded in [docs/decisions](docs/decisions/README.md).
  Add a record when a choice is expensive to reverse; never rewrite an accepted one, supersede it with a new record.
- **Comments say why**, never what changed or when. Git history holds that.
