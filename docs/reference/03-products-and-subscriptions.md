# Products, subscriptions and webhooks

> Part 3 of the Auth Service reference. Overview, glossary and symptom index: [README](README.md).

A *product* (called `Application` in code) is a separate system that trusts the Auth Service. This part covers everything that makes a
product exist on the platform and lets a tenant use it: who may manage products (the platform admin), how a product is registered and
given a permission catalog, how it proves who it is to the Auth Service (client credentials and the service token), where it is opened
from, how a tenant subscribes to it, and how the Auth Service tells the product about it (signed webhooks).

## Contents

- [Platform admin role and admin API group](#platform-admin-role-and-admin-api-group)
- [Product registration and the admin product list](#product-registration-and-the-admin-product-list)
- [Product permission catalogs and activation](#product-permission-catalogs-and-activation)
- [Product limit definitions](#product-limit-definitions)
- [Plans](#plans)
- [Plan assignment, overrides and effective entitlements](#plan-assignment-overrides-and-effective-entitlements)
- [Entitlement events](#entitlement-events)
- [Entitlement reads](#entitlement-reads)
- [Entitlement portal reads](#entitlement-portal-reads)
- [Product credentials and the service token](#product-credentials-and-the-service-token)
- [Launch URL and the "my products" read](#launch-url-and-the-my-products-read)
- [Subscriptions](#subscriptions)
- [Webhooks to products and webhook secret rotation](#webhooks-to-products-and-webhook-secret-rotation)
- [Symptom to where to look (this part)](#symptom-to-where-to-look-this-part)
- [Highest-risk areas in this part](#highest-risk-areas-in-this-part)
- [Unknowns and assumptions to confirm](#unknowns-and-assumptions-to-confirm)

Conventions used below: "the README" means the repository `README.md`; story codes (APP-nn, SUB-nn) are the ones quoted in code comments;
tables are PostgreSQL tables with snake_case names. Every limit quoted exists in the code named next to it.

---

## Platform admin role and admin API group

### What it does

A few operations belong to whoever runs the platform, not to any tenant: registering products, activating them, rotating their secrets,
rotating signing keys, reading the whole audit log, and resending failed webhooks. A **platform admin** is an ordinary user whose row in
`users` has `is_platform_admin = true`. Only a platform admin can call anything under `/api/auth/admin`.

- **The flag can only be set in the database.** No endpoint, request type or response type has a field for it (a test,
  `APP_TC10_TheFlagCannotBeSetThroughAnyApi`, scans every exported `*Request`/`*Response` type for the name). New users are `false`
  (`UserConfiguration`: `IsRequired().HasDefaultValue(false)`). Making someone an admin is SQL, for example
  `UPDATE users SET is_platform_admin = true WHERE email = 'ops@example.com';` (`docs/deployment.md`, step "Make the first platform admin").
- **It is read from the database on every request, never from the token.** Granting or revoking takes effect on the very next call, on the
  same token (`APP04_GrantingAndRevokingTheFlagTakesEffectAtOnceOnTheSameToken`). No token can carry the privilege.
- **It opens the admin area and nothing else.** An admin has the same view of tenants as any user (`APP04_TheFlagIsFalseForEveryNewUserAndGrantsNothingOutsideTheAdminArea`).
- **Only an identity token works.** A token that has an `aud` claim (a product token or a service token) is refused even if its user is an
  admin (`APP04_AProductTokenIsRefusedEvenForAnAdmin`).
- **Every admin action is audited under the admin's identity.** Changing the flag itself is not (it is a direct SQL update).

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| any verb under `/api/auth/admin/*` | Policy `PlatformAdmin` (`IdentityAuthentication.PlatformAdminPolicyName`) | the endpoint's own handler | `PlatformAdminHandler.HandleRequirementAsync` | Lets the request through only when the caller's identity token is valid and `users.is_platform_admin` is true. |

The routes in the group, with the part that documents each:

| Route | Documented in |
|---|---|
| `POST /api/auth/admin/signing-keys/rotate` (`SigningKeyAdminEndpoints`) | signing keys part |
| `GET /api/auth/admin/audit` (`AuditAdminEndpoints`) | audit part |
| `GET`/`POST /api/auth/admin/applications`, `PUT .../launch-url`, `GET`/`POST .../permissions`, `PUT .../active`, `POST .../secret/rotate` (`ApplicationAdminEndpoints.MapApplicationsAdmin`) | this part |
| `GET /api/auth/admin/webhooks`, `POST /api/auth/admin/webhooks/{webhookId}/resend` (`WebhookAdminEndpoints.MapWebhooksAdmin`) | this part, last section |

Flow of a request to an admin route:

1. `Program.cs` creates the group once: `var admin = app.MapAdminGroup();` (`src/AuthService.Api/Features/Admin/AdminEndpoints.cs`), which is
   `app.MapGroup("/api/auth/admin").RequireAuthorization("PlatformAdmin")`. Each feature then maps into `admin` (`MapSigningKeyAdmin`,
   `MapAuditAdmin`, `MapApplicationsAdmin`, `MapWebhooksAdmin`). Because the policy is on the group, a new endpoint added there cannot be left
   open by forgetting to protect it. `APP04_EveryEndpointUnderTheAdminPathRequiresThePlatformAdminPolicy` enumerates the routes to prove it.
2. `app.UseAuthentication()` validates the bearer token (`IdentityAuthentication.AddIdentityTokenAuthentication`): signature against the
   published signing keys chosen by `kid`, algorithm ES256 only, issuer equal to `Auth:Tokens:Issuer`, lifetime with a fixed 30 second
   clock skew. A bad or missing token ends in `OnChallenge`, which writes `401` with `code` `unauthorized`.
3. `app.UseAuthorization()` evaluates the `PlatformAdmin` policy: the shared identity-token rules (authenticated, has a `sub` claim, has **no**
   `aud` claim) plus `PlatformAdminRequirement`.
4. `PlatformAdminHandler` (`src/AuthService.Api/Tokens/PlatformAdminAuthorization.cs`) reads the `sub` as the user id and runs one query,
   `db.Users ... Select(user => user.IsPlatformAdmin).SingleOrDefaultAsync()`. If true it calls `context.Succeed`. It never calls `Fail`;
   anything else simply leaves the requirement unmet, which the framework turns into `403`.
5. The handler of the endpoint runs. Handlers read the admin's id from the token (`caller.GetUserId()`) to record the audit actor.

Data: `users.is_platform_admin` (boolean, not null, default false; entity `User.IsPlatformAdmin`; added by migration
`20261031200355_AddPlatformAdminFlag`). Libraries: ASP.NET Core authorization and JwtBearer, `Microsoft.IdentityModel`.

The Blazor console (`src/AuthService.Web`) decides whether to show `/platform` by asking the API, never by trusting a cookie:
`PlatformSession` calls `GET /api/auth/admin/webhooks?limit=1` ("the cheapest read behind the platform-admin policy that is not itself
recorded in the audit log"). 401 means the session ended, 403 means "not allowed".

Background workers: none.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:Issuer` / `Auth__Tokens__Issuer` | `AuthService` | `IdentityTokenOptions` | Tokens are validated against it. A changed value after tokens were issued makes them all fail with 401. |
| `Auth:Tokens:AccessTokenMinutes` / `Auth__Tokens__AccessTokenMinutes` | 15 | `IdentityTokenOptions` | How long an admin's identity token lasts. |
| `Auth:Tokens:KeyEncryptionKey` / `Auth__Tokens__KeyEncryptionKey` | none; required outside Development and Testing | `IdentityTokenOptions`, `KeyEncryption` | Without it the service refuses to start (signing keys cannot be read). |
| Clock skew | 30 seconds, hard-coded | `IdentityAuthentication` (`ClockSkew`) | Not configurable. |

There is no setting for the flag, for a list of admins or for turning the admin area off.

### Failure modes and production debugging

- **No token, malformed token, expired token, wrong issuer, unknown `kid`:** `401`, problem body with `code` `unauthorized`,
  `WWW-Authenticate: Bearer`.
- **Valid identity token, user not an admin (or the user row does not exist):** `403` with **no problem body and no `code`**. There is no
  custom forbidden handler (no `IAuthorizationMiddlewareResultHandler` in the code), so this is the framework's empty 403.
- **Service token or product token:** `403` (same empty body), because it carries an `aud` claim.
- Logged: nothing specific to a refusal. Audit: none for refusals. The audit log records what admins do (for example `auth.application.registered`).
- Likely production causes, with the check:
  - The UPDATE was run against the wrong database, or the wrong email: `SELECT email, is_platform_admin FROM users WHERE email = 'ops@example.com';`
    (`email` is `citext`, so case does not matter).
  - A new admin "still gets 403 after the update": this is not a token problem (the flag is not in the token). Check the request really goes to
    the database you updated, and that the console is not showing a cached "not allowed" page for the same request.
  - A token that expired (15 minutes by default): the symptom is 401, not 403.
  - A caller using a product token (for example one copied from the picker hand-off): 403.
- Edge case: the console's probe is `GET /api/auth/admin/webhooks`; if that endpoint fails for another reason (database down) the console
  reports "unavailable", not "not allowed".

### Decisions and open questions

- **Why the flag is not a claim:** so revoking it is immediate and no token, however it was made, can grant it (comment in
  `PlatformAdminAuthorization.cs`). The price is one database query per admin request.
- **Why the whole group has one policy:** a forgotten attribute cannot expose an admin route.
- **Inconsistent documentation:** `docs/deployment.md` (first-deployment step "Make the first platform admin") says "Log in again for a token
  that carries it". The code does not put the flag in the token, so logging in again is not needed. The README says the opposite and is right.
- **Risk:** granting the flag leaves no audit entry (direct SQL), and there is no record of who holds it other than the `users` table.
  There is also no guard against removing the last admin; both are manual database operations.
- **Risk:** a platform admin can do everything in the admin area (including rotating signing keys, which invalidates every token). There is no
  finer-grained role.
- **Assumption (confirm):** the empty 403 body (no `code`) is acceptable to clients. The console only looks at the status. I assumed that because
  no handler adds a body, not because a document says so.

---

## Product registration and the admin product list

### What it does

A platform admin registers a new product by giving it a key, a display name and the audience its tokens will carry. The Auth Service
creates the product **inactive** and returns its client id and client secret **once**. The same call may also carry the product's launch
address, its permission catalogue and a request to make it available (ONB-01), so a product can be ready in one step. The admin can then
list every product to see where it stands, and rename one.

Rules (all in `ApplicationValidator`, `RegisterApplicationService.cs`):

- **Key:** required, trimmed and lowercased, 1 to 63 characters (`MaxKeyLength = 63`), pattern `^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$` (lowercase
  letters, digits and hyphens; starts and ends with a letter or digit). Unique across the platform.
- **Name:** required, trimmed, at most 200 characters (`MaxNameLength`).
- **JWT audience:** required, trimmed, at most 200 characters (`MaxAudienceLength`), pattern `^[A-Za-z0-9._:/-]+$` (no spaces). Unique across
  the platform. The value `auth-service` (`IdentityTokenService.ServiceAudience`, compared ignoring case) is reserved because service
  tokens carry it.
- The built-in product's key `auth-service` and audience `auth-service-tenant` (`BuiltInApplication.Key`, `BuiltInApplication.Audience`) are
  already stored, so registering them gives `409`.
- A new product is `is_active = false` until it has a permission catalog and an admin activates it (next section), unless the registration
  itself sent a catalogue and `activate: true`.
- **Optional parts of the registration body (ONB-01):** `launchUrl` (the rules of the launch URL section), `permissions` (the same entries and
  rules as the catalogue registration, so up to 200; an empty list counts as none) and `activate` (true needs at least one permission, else
  `400` on `Activate`). They are validated with the product's fields and saved with it in one `SaveChangesAsync`: if anything is invalid or
  the key or audience is taken, nothing is created. The audit log gets the same entries the separate calls write: `registered`,
  `permissions_registered` (only with permissions), `launch_url_set` (only with an address) and `activated` (only when activated).
- **Role templates (ONB-04):** the same body may carry `roles`, the roles the product ships with, which must grant only keys of the `permissions` sent in
  the same request (else `400` on `Roles[n].Permissions[m].Key`). They are saved with the product and audited as `auth.application.role_templates_set`.
  Their rules, the copy into an organisation when it first subscribes and the admin routes are in the README ("Roles a product ships with"):
  `RoleTemplateService` (`PUT` / `GET /api/auth/admin/applications/{id}/role-templates`, `POST .../role-templates/apply`), `RoleTemplateValidator`,
  `RoleTemplateCopier` (called by `SubscribeTenantService` for a new subscription only, in the same transaction).
- **Rename (ONB-01):** `PUT /api/auth/admin/applications/{id}/name` with `{"name": "…"}` changes the display name only, under the name rules
  above. The key and the audience never change. Saying the current name again is `200` and leaves no audit entry. The built-in product and
  an unknown id are `404 application_not_found`.
- The client secret is shown in the registration response **only**. Only its SHA-256 hash is stored (`application_secrets.secret_hash`).
  The response is sent with `Cache-Control: no-store`. A lost secret is replaced by rotation, never recovered.
- The admin product list shows every product that is not built in, active or not, ordered by name: `id`, `key`, `name`, `isActive`,
  `permissionCount`, `launchUrl`, `createdAt`. It never returns the audience, client id, client secret or webhook address.
- There is **no route** to delete a product or change its key or audience. `MapApplicationsAdmin` has eight routes; the only one that edits
  a product's own fields is the rename of its display name.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/admin/applications` | platform admin | `ApplicationAdminEndpoints.RegisterAsync` | `ApplicationValidator.TryValidate`, `RegisterApplicationService.RegisterAsync` | Validates, creates the product, its first secret and (when sent) its permissions, launch address and activation, audits. `201` with the credentials. |
| `PUT /api/auth/admin/applications/{id}/name` | platform admin | `ApplicationAdminEndpoints.RenameAsync` | `ApplicationValidator.TryValidateName` | Changes the display name, audits (`auth.application.renamed`, detail = key). `200` with `id`, `key`, `name`. |
| `GET /api/auth/admin/applications` | platform admin | `ApplicationAdminEndpoints.ListAsync` | `PageQuery.ByNameAsync` | Pages every non-built-in product by name. Query `limit` (1 to 200, default 50), `after` and `afterId` together. |

Flow of registration:

1. `RegisterAsync` validates the body. Failure: `Results.ValidationProblem` (400) keyed by `Key`, `Name`, `JwtAudience`, `LaunchUrl`,
   `Permissions[n].Key` / `.Label` / `.Description` and `Activate`; nothing is created.
2. `RegisterApplicationService.RegisterAsync` does a friendly pre-check for an existing key, then for an existing audience.
3. It builds the entity: `Id = Guid.CreateVersion7()`, `ClientId = "cl_" + base64url(16 random bytes)`, `IsActive = false`, and a client secret
   `SecretToken.NewValue()` (base64url of 32 random bytes). It adds an `ApplicationSecret` with `SecretToken.Hash(secret)` (SHA-256, 64
   uppercase hex characters) and `ExpiresAt = null`.
   When the request sent them it also sets `IsActive` and `LaunchUrl` and adds one `Permission` row per entry.
4. It records the audit entry `AuditActions.ApplicationRegistered` (`auth.application.registered`), actor = the admin, detail = the key, and
   the entries for the permissions, launch address and activation that were sent.
5. `SaveChangesAsync`. If two registrations race, the unique indexes decide: a `UniqueViolation` on `ux_applications_key` becomes
   `KeyTaken`, on `ux_applications_jwt_audience` becomes `AudienceTaken` (`BrokeUniqueRule`), so the same 409 is returned.
6. The handler sets `Cache-Control: no-store` and answers `201` with `RegisterApplicationResponse`
   (`id`, `key`, `name`, `jwtAudience`, `isActive`, `clientId`, `clientSecret`, `createdAt`, `launchUrl`, `permissionCount`, `credential`).

Data (entity `Application`, table `applications`; `ApplicationSecret`, table `application_secrets`):

| Table.column | Meaning |
|---|---|
| `applications.id`, `key`, `name`, `jwt_audience`, `client_id` | Identity. Unique indexes: `ux_applications_key`, `ux_applications_jwt_audience`, and one on `client_id`. |
| `applications.is_active`, `is_built_in` | State flags. |
| `applications.launch_url`, `webhook_url`, `webhook_secret_protected`, `webhook_previous_secret_protected`, `webhook_previous_secret_expires_at` | Covered in later sections. |
| `application_secrets.application_id`, `secret_hash` (unique), `created_at`, `expires_at` | One row per client secret. `expires_at` null = the current secret. |

States: inactive on creation; active/inactive afterwards through the activation route. Libraries: EF Core with Npgsql,
`Microsoft.IdentityModel.Tokens` (`Base64UrlEncoder`). Background workers: none.

### Configuration and environment

None. There is no setting for registration, and no limit on how many products exist.

### Failure modes and production debugging

| Caller sees | When |
|---|---|
| `400` validation problem (field names `Key`, `Name`, `JwtAudience`, `LaunchUrl`, `Permissions[n].…`, `Activate`) | Missing or invalid value, over-long value, reserved audience `auth-service`, a bad launch address or permission, or `activate: true` without permissions. Nothing is created. |
| `409` `application_key_taken` ("Application key already registered") | Key already used, including `auth-service` and a key differing only by case. |
| `409` `audience_taken` ("JWT audience already registered") | Audience already used, including `auth-service-tenant`. |
| `401` / `403` | See the platform admin section. |
| `201` | Created. Body has the secret; `Cache-Control: no-store`. |

- Audit: `auth.application.registered` (detail = key). The secret never reaches the audit log or the logs (`APP01_TheSecretIsNeverStoredOrLoggedInPlainText`).
- Edge cases: the key order check runs before the audience check, so a request with both taken reports the key. `Cache-Control: no-store` is
  set after validation but before the result is chosen, so it is on the 201 and the 409 answers (not on a 400).
- Half-state: a product that was registered but whose secret was lost has no recovery path except rotation (`POST .../secret/rotate`). A
  product registered with a typo in the audience cannot be corrected through the API (no edit route); it needs a database change, and
  tokens already issued for that audience would stop matching.
- Confirm a registration: `SELECT id, key, jwt_audience, client_id, is_active, is_built_in, created_at FROM applications ORDER BY created_at DESC;`
  and `SELECT application_id, created_at, expires_at FROM application_secrets WHERE application_id = '<id>';`.

### Decisions and open questions

- Unique audience per product so that no two products can accept each other's tokens (comment on `Application.JwtAudience`).
- The secret is hashed with plain SHA-256 because it is 256 bits of randomness (comment on `ApplicationSecret.SecretHash`).
- **Open:** no delete of a product, and no edit of its key or audience. A mistake in those needs SQL; the display name can be renamed
  (ONB-01, because branding changes while the key and audience are what a product's code and tokens depend on). I found no ticket that says
  the rest is intended.
- **Assumption (confirm):** audience and key uniqueness are case-sensitive for the audience (the key is lowercased in code; the audience is
  not) because the pre-check is an equality comparison and the unique index uses PostgreSQL's default collation. I did not see the database
  collation set anywhere; if it were case-insensitive, `Acme-API` and `acme-api` would collide.
- **Open:** expired rows in `application_secrets` are never removed. I found no code that deletes them.

---

## Product permission catalogs and activation

### What it does

A product owns a **catalog** of the things it can control, such as `campaign.create`. Tenant admins later build roles out of these
permissions. A platform admin registers the catalog, can register it again with changes, reads it back, and finally makes the product
**active** so tenants can subscribe to it. A product with no permissions cannot be activated, because a tenant would be subscribing to
nothing it can grant.

Rules (`PermissionCatalogValidator`, `PermissionCatalogService`):

- 1 to 200 permissions per request (`MaxPermissionsPerRequest = 200`). An empty or missing list is `400`.
- **Key:** trimmed, lowercased, 1 to 100 characters (`MaxKeyLength = 100`), pattern `^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$`. Unique **within the
  product** (index `ux_permissions_application_id_key`), so two products may both have `report.export`. A key repeated inside one request is `400`.
- **Label:** required, at most 200 characters. **Description:** optional, at most 1000 characters; blank becomes null.
- Any invalid entry rejects the **whole** request with `400`; nothing is saved.
- **Upsert, never delete.** A new key is created. An existing key whose label or description differs is **updated in place**, keeping its
  `Id` (roles refer to the id, so grants survive a relabel). An identical entry is left alone. A key a product stops sending stays in
  the catalog.
- Response: `201` when the request created at least one key, otherwise `200`. Body: `applicationId`, `created`, `updated`, `unchanged` and the **whole**
  catalog afterwards (`id`, `key`, `label`, `description`), ordered by key.
- **Activation:** `PUT .../active` with `{"isActive": true}` needs at least one permission. `false` withdraws the product and is allowed at
  any time. Repeating the same value is harmless and is not audited again.
- The catalog can be read by a platform admin whether or not the product is active.
- The built-in Auth Service product is never found by these routes (its catalog is defined in code, see `BuiltInApplication.Permissions`).

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/admin/applications/{applicationId}/permissions` | platform admin | `RegisterPermissionsAsync` | `PermissionCatalogValidator.TryValidate`, `PermissionCatalogService.RegisterAsync` | Upsert of the catalog. `201`/`200`, `400`, `404 application_not_found`. |
| `GET /api/auth/admin/applications/{applicationId}/permissions` | platform admin | `ListPermissionsAsync` | `PageQuery.ByNameAsync` | Pages the catalog by key: `key`, `label`, `description`, `createdAt`, `updatedAt`. `404` for unknown and built-in. |
| `PUT /api/auth/admin/applications/{applicationId}/active` | platform admin | `SetActiveAsync` | `PermissionCatalogService.SetActiveAsync` | Activates or withdraws. `200` `{id, key, isActive}`. |

Flow of registration:

1. `PermissionCatalogValidator.TryValidate` normalises keys and checks every entry.
2. `PermissionCatalogService.RegisterAsync` opens a transaction and calls `db.LockApplicationAsync`
   (`SELECT * FROM applications WHERE id = {id} AND NOT is_built_in FOR UPDATE`, `ApplicationLocking.cs`). Registrations, activations and
   secret rotations of one product therefore take turns. No row means `ApplicationNotFound` (404, also for the built-in product).
3. It loads the product's current catalog into a dictionary and, per input, creates, updates (setting `UpdatedAt`) or counts it as unchanged.
4. It records `AuditActions.PermissionsRegistered` (`auth.application.permissions_registered`, detail = product key) even when nothing changed.
5. `SaveChangesAsync` and commit. A unique violation on `ux_permissions_application_id_key` is rethrown as `InvalidOperationException`
   ("A permission key was inserted twice for one application.") because the lock makes it unreachable.

Flow of activation: lock the product; if `isActive` is true and no row in `permissions` has this `application_id`, return
`NoPermissions` (`409 no_permissions_registered`); if the value changed, set `IsActive` and record `auth.application.activated` or
`auth.application.deactivated` (detail = key); commit. A missing `isActive` field is `400` ("IsActive is required.").

Data: `permissions` (entity `Permission`): `id`, `application_id`, `key`, `label`, `description`, `created_at`, `updated_at`; FK to `applications`
with restrict delete. `applications.is_active`. Libraries: EF Core, Npgsql. Background workers: none.

What `is_active` controls elsewhere (so you know what a withdrawal affects):

| Place | Effect of `is_active = false` |
|---|---|
| `GET /api/auth/applications` and `GET /api/auth/users/me/applications` | The product is not listed. |
| `SubscribeTenantService.SubscribeAsync` | `400 application_not_active`. |
| `TokenExchangeService.ExchangeAsync` | `403 exchange_denied` for everyone (the query filters `IsActive`). |
| Existing subscriptions, seats, webhooks, service token | Untouched. The product can still get a service token and still receives events. |

### Configuration and environment

None.

### Failure modes and production debugging

| Caller sees | When |
|---|---|
| `400` validation problem (`Permissions`, `Permissions[n].Key`, `.Label`, `.Description`) | Empty list, more than 200, bad key/label/description, duplicate key in the request. |
| `404` `application_not_found` | Unknown product id or the built-in product (register, read, activate). |
| `409` `no_permissions_registered` | Activating a product with an empty catalog. |
| `400` (`IsActive` required) | Body without `isActive`. |

- Audit: `auth.application.permissions_registered`, `auth.application.activated`, `auth.application.deactivated`.
- Edge cases:
  - **Re-registering without a description wipes the stored one.** The comparison is `permission.Description != input.Description`; an omitted
    description is `null`, so it counts as an update to null. A "relabel" call must resend the description.
  - Keys are lowercased on the way in. A product that checks `Campaign.Create` in code will never match the stored `campaign.create`.
  - A removed key stays forever and still appears in tenants' role editors (the API has no delete).
  - A withdrawn product keeps its subscriptions, but nobody can open it (exchange is denied) and nobody new can subscribe.
- Likely production issues and how to confirm:
  - "Cannot activate": `SELECT count(*) FROM permissions WHERE application_id = '<id>';`
  - "A tenant cannot subscribe, `application_not_active`": `SELECT key, is_active FROM applications WHERE key = '<key>';`
  - "A relabel did not show": `SELECT key, label, description, updated_at FROM permissions WHERE application_id = '<id>' ORDER BY key;`.

### Decisions and open questions

- Update in place instead of delete and recreate, so role grants keep pointing at the same permission id.
- Never remove a key, because a role may still grant it.
- The row lock on the product serialises catalog changes and prevents two registrations inserting the same new key.
- **Open:** no way to remove a stale permission from the catalog (by design, but it grows without bound).
- **Open:** `auth.application.permissions_registered` is written even when `created = updated = 0`; the audit log therefore cannot tell a real
  change from a repeat.

---

## Product limit definitions

### What it does

The first part of plans and entitlements (ENT-01, epic AUTH-277). A product names the **numbers it limits**, such as `contacts.reachable` or
`storage.assets`. The Auth Service only knows the key, a label, a display unit and a **default value**; the product decides what a key means and
counts its own usage. Plans (ENT-02) and per-tenant overrides (ENT-03) give the keys values later. A platform admin adds and changes limits; the
product reads its own with its service token.

Rules (`LimitDefinitionValidator`, `LimitDefinitionService`):

- 1 to 100 limits per request, and at most 100 per product in total (`MaxLimitsPerProduct = 100`). A request that would pass 100 is `409 too_many_limits`
  and saves nothing; changing limits already there is always allowed.
- **Key:** the same rules as a permission key (trimmed, lowercased, 1 to 100 characters, pattern `^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$`), unique within
  the product (index `ux_limit_definitions_application_id_key`). A key repeated in one request is `400`.
- **Label:** required, at most 200 characters. **Description:** optional, at most 1000. **Unit:** optional display text, at most 50 (`MaxUnitLength`).
- **Default value:** required, a whole number of at least 0, or `null` for unlimited (`TryReadValue`). Leaving it out is `400`, because a missing value
  must never quietly mean "unlimited". A decimal, a string or a negative number is `400`. The column also has a check constraint
  (`ck_limit_definitions_default_value_not_negative`).
- **On/off features:** a limit whose unit is `flag` (any case, `FlagUnit`) can only default to `0` (off) or `1` (on).
- **Seats:** the key `seats` (`SeatsKey`) becomes the subscription's seat limit in ENT-03, which is at least 1, so a `seats` value of 0 is refused here and in plans (`SeatsRule`).
- **Upsert, never delete or rename.** A new key is created; an existing key with any difference (label, description, unit, default) is updated in place;
  an identical entry is left alone. A field left out (description, unit) is cleared, so send the whole limit each time. `201` when something was added,
  otherwise `200`. The body is `applicationId`, `created`, `updated`, `unchanged` and every limit of the product by key.
- Only a request that changed something writes the audit entry `auth.application.limits_registered` (detail = product key).
- The built-in Auth Service product is never found by these routes.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/admin/applications/{applicationId}/limits` | platform admin | `LimitDefinitionEndpoints.RegisterAsync` | `LimitDefinitionValidator.TryValidate`, `LimitDefinitionService.RegisterAsync` | Upsert. `201`/`200`, `400`, `404 application_not_found`, `409 too_many_limits`. |
| `GET /api/auth/admin/applications/{applicationId}/limits` | platform admin | `LimitDefinitionEndpoints.ListAsync` | `PageQuery.ByNameAsync` | Pages by key: `key`, `label`, `description`, `unit`, `defaultValue`, `createdAt`, `updatedAt`. `404` for unknown and built-in. |
| `GET /api/auth/applications/me/limits` | service token | `LimitDefinitionEndpoints.ListOwnAsync` | `PageQuery.ByNameAsync` | The same page, for the product named by the token only. |

Flow of registration: validate every entry; open a transaction and `db.LockApplicationAsync` (so registrations of one product take turns); load the
product's limits; refuse with `TooMany` if the new keys would pass 100; create, update or count each entry; audit and save only when something changed;
commit.

Data: `limit_definitions` (entity `LimitDefinition`): `id`, `application_id`, `key`, `label`, `description`, `unit`, `default_value` (bigint, null =
unlimited), `created_at`, `updated_at`; FK to `applications` with restrict delete. Migration `AddLimitDefinitions`.

### Configuration and environment

None.

### Failure modes and production debugging

| Caller sees | When |
|---|---|
| `400` validation problem (`Limits`, `Limits[n].Key`, `.Label`, `.Description`, `.Unit`, `.DefaultValue`) | Empty list, more than 100, bad field, missing or wrong default, duplicate key, a `flag` default other than 0 or 1. |
| `404` `application_not_found` | Unknown product id or the built-in product. |
| `409` `too_many_limits` | The product would have more than 100 limits. |

- A product that reads `GET /api/auth/applications/me/limits` with a person's token gets `403`: only a service token is accepted.
- "A limit did not change": `SELECT key, label, unit, default_value, updated_at FROM limit_definitions WHERE application_id = '<id>' ORDER BY key;`

### Decisions and open questions

- Same shape as the permission catalog on purpose: upsert by key, never removed, so plans and overrides can refer to keys safely.
- The default must be sent outright (decision in the epic AUTH-277). Changing a default will change tenants' effective limits once ENT-03 exists.

---

## Plans

### What it does

ENT-02 (epic AUTH-277). A plan is a named template of limit values for one product. A platform admin creates, replaces, activates or deactivates
and deletes plans; the product reads its own. Plans only exist in the catalogue until ENT-03 lets a subscription point at one.

Rules (`PlanValidator`, `PlanService`):

- **Key:** the limit-key rules (`LimitDefinitionValidator.IsValidKey`), lowercased, unique within the product (index `ux_plans_application_id_key`,
  `409 plan_key_taken`). Never renamed. At most 20 plans per product (`MaxPlansPerProduct`, `409 too_many_plans`).
- **Name:** required, at most 200 characters. **Description:** optional, at most 1000; left out on a replace means cleared.
- **Values:** an object of limit key to a whole number of at least 0 or `null` (unlimited), read with `LimitDefinitionValidator.TryReadValue`. The object is
  required (send `{}` for none). A limit left out takes the limit's default. A key that is not a limit of this product is `400 limit_unknown` naming
  the keys; a `flag` limit only takes 0 or 1 (`400 value_invalid`).
- **Replace** (`PUT .../plans/{planKey}`) sets the name, description and the whole set of values; a key no longer listed loses its row in `plan_values`.
- **Active:** a deactivated plan cannot be newly assigned (ENT-03); tenants that have it keep it. Repeating a state is not audited.
- **Delete:** only while `plans.ever_assigned` is false (set by ENT-03 on first assignment, never cleared); otherwise `409 plan_in_use`. The plan's
  values are deleted with it (cascade).
- Audit: `auth.plan.created`, `auth.plan.updated` (only when something changed), `auth.plan.activated`, `auth.plan.deactivated`, `auth.plan.deleted`,
  detail `<product key>.<plan key>`.
- All changes of one product run behind its row lock (`db.LockApplicationAsync`), so the cap and key uniqueness hold under concurrent calls.

### How it is implemented

| Verb and route | Auth required | Handler | What it does |
|---|---|---|---|
| `POST /api/auth/admin/applications/{applicationId}/plans` | platform admin | `PlanEndpoints.CreateAsync` → `PlanService.CreateAsync` | `201` with the plan; `400`, `404`, `409`. |
| `GET /api/auth/admin/applications/{applicationId}/plans` | platform admin | `PlanEndpoints.ListAsync` | Paged by key. |
| `GET /api/auth/admin/applications/{applicationId}/plans/{planKey}` | platform admin | `PlanEndpoints.GetAsync` | One plan; `404 plan_not_found`. |
| `PUT /api/auth/admin/applications/{applicationId}/plans/{planKey}` | platform admin | `PlanEndpoints.ReplaceAsync` → `PlanService.ReplaceAsync` | `200` with the plan. |
| `PUT /api/auth/admin/applications/{applicationId}/plans/{planKey}/active` | platform admin | `PlanEndpoints.SetActiveAsync` → `PlanService.SetActiveAsync` | `200` with the plan. |
| `DELETE /api/auth/admin/applications/{applicationId}/plans/{planKey}` | platform admin | `PlanEndpoints.DeleteAsync` → `PlanService.DeleteAsync` | `204`, `409 plan_in_use`. |
| `GET /api/auth/applications/me/plans` | service token | `PlanEndpoints.ListOwnAsync` | The token's own product only, paged. |

A plan is returned as `PlanDto`: `key`, `name`, `description`, `isActive`, `canDelete` (= not `ever_assigned`), `values` (only the limits it sets, by key),
`createdAt`, `updatedAt`.

Data: `plans` (entity `Plan`): `id`, `application_id`, `key`, `name`, `description`, `is_active`, `ever_assigned`, `created_at`, `updated_at`.
`plan_values` (entity `PlanValue`): `id`, `plan_id` (cascade), `limit_definition_id` (restrict), `value` (bigint, null = unlimited, check
`ck_plan_values_value_not_negative`), unique on (`plan_id`, `limit_definition_id`). Migration `AddPlans`.

### Failure modes and production debugging

| Caller sees | When |
|---|---|
| `400` validation problem (`Key`, `Name`, `Description`, `Values`, `Values.<key>`) | Bad key, missing name or values, a negative, decimal or text value. |
| `400` `limit_unknown` / `value_invalid` | A key that is not a limit of the product / a `flag` value other than 0 or 1. |
| `404` `application_not_found` / `plan_not_found` | Unknown or built-in product / no plan with that key. |
| `409` `plan_key_taken` / `too_many_plans` / `plan_in_use` | Duplicate key / 21st plan / deleting a plan that was ever assigned. |

- "What does Growth set?": `SELECT l.key, v.value FROM plan_values v JOIN limit_definitions l ON l.id = v.limit_definition_id JOIN plans p ON p.id = v.plan_id WHERE p.key = 'growth';`

### Decisions and open questions

- Delete only if never assigned, deactivate otherwise (decision 4 in the epic AUTH-277), so history always names a plan that exists.
- Replacing a plan's values does not yet change any tenant: ENT-03 adds versions and ENT-04 the events a plan edit sends.

---

## Plan assignment, overrides and effective entitlements

### What it does

ENT-03 (epic AUTH-277, decision 0014). A platform admin puts a tenant's subscription on a plan and sets per-tenant overrides. The Auth Service
computes the tenant's **effective** limits (override, else plan value, else the limit's default; every limit always present), raises the
subscription's version when they change, keeps `maxSeats` equal to the effective `seats`, and records every change in an entitlement history.

Rules (`EffectiveEntitlements`, `EntitlementState`, `SubscriptionEntitlementService`):

- **Assign** (`PUT .../plan`): an existing plan of the product (`404 plan_not_found`) that is active (`409 plan_inactive`), unless the
  subscription already has it; `{"planKey": null}` removes the plan. Assigning sets `plans.ever_assigned` for good. The same plan again is a no-op.
- **Override** (`PUT .../overrides/{limitKey}`): `value` must be present (null = unlimited, else a whole number ≥ 0, `flag` 0 or 1, `seats` ≥ 1);
  `reason` 1 to 200 characters (`MaxReasonLength`); the limit must exist (`404 limit_unknown`); at most 100 per subscription
  (`MaxOverridesPerSubscription`, `409 too_many_overrides`). The same value and reason again is a no-op. **Clear** (`DELETE`) is `404 override_not_found` when absent.
- **Versions** (`EntitlementState.CommitAsync`): callers snapshot the subscriptions a change can reach, save, then commit. A subscription whose set
  differs (`EffectiveEntitlements.Differ`: a value, or whether a limit is overridden), or that had no snapshot (a new subscription), gets
  `entitlements_version + 1` and `entitlements_change_seq = nextval('entitlement_change_seq')` in one `UPDATE ... RETURNING`, after locking those
  rows and then the product's change-number lock (decision 0015, see "Entitlement reads").
- **Who triggers a recompute:** plan assignment and overrides (one subscription); `PlanService.ReplaceAsync` when values change (every subscription
  on the plan); `LimitDefinitionService.RegisterAsync` when a limit is added or a default changes (every subscription of the product);
  `SubscribeTenantService` for a new subscription; `SeatLimitService` through a `seats` override.
- **Seats** (`EntitlementState.SyncSeatsAsync`): only when the product defines `seats`. The effective value is written to `subscriptions.max_seats`
  (null stays null). `PATCH .../seats` and `maxSeats` on subscribe become a `seats` override with `SeatLimitService.SeatsOverrideReason`, set by
  the product.
- **History** (`EntitlementHistory.Record`, table `entitlement_changes`): codes in `EntitlementChanges` (`limit_added`, `limit_changed`,
  `plan_created`, `plan_updated`, `plan_activated`, `plan_deactivated`, `plan_deleted`, `plan_assigned`, `override_set`, `override_cleared`), old and
  new values as JSON, reason, actor. Written in the same transaction as the change. No foreign keys, never deleted (yet).
- Audit: `auth.subscription.plan_assigned`, `auth.subscription.override_set`, `auth.subscription.override_cleared` (tenant = the tenant, detail =
  product key). Reasons never go to the audit log.

### How it is implemented

| Verb and route | Auth required | Handler | What it does |
|---|---|---|---|
| `GET /api/auth/admin/tenants/{tenantId}/subscriptions/{applicationId}/entitlements` | platform admin | `SubscriptionEntitlementEndpoints.GetAsync` → `SubscriptionEntitlementService.ReadAsync` | `SubscriptionEntitlementsResponse`; `404 subscription_not_found`. |
| `PUT .../plan` | platform admin | `AssignPlanAsync` | `200` with the entitlements. |
| `PUT .../overrides/{limitKey}` | platform admin | `SetOverrideAsync` → `SubscriptionEntitlementService.SetOverrideAsync` / `ApplyOverrideAsync` | `200`, `400`, `404`, `409`. |
| `DELETE .../overrides/{limitKey}` | platform admin | `ClearOverrideAsync` | `200`, `404 override_not_found`. |
| `GET /api/auth/admin/entitlement-history` | platform admin | `HistoryAsync` | `PageQuery.NewestFirstAsync`; filters `applicationId`, `tenantId`, `planKey`. |

Changes to one subscription take `LockSubscriptionAsync` (the advisory lock subscribing uses). Data: `subscriptions.plan_id` (FK `plans`,
restrict), `entitlements_version`, `entitlements_change_seq` (index with `application_id`); `subscription_overrides` (unique per subscription and
limit, cascade with the subscription, `reason` up to 200); `entitlement_changes` (`old_value`/`new_value` jsonb). Migration `AddSubscriptionEntitlements`.

### Failure modes and production debugging

| Caller sees | When |
|---|---|
| `400` validation problem (`Value`, `Reason`) | Missing or negative value, missing or over-long reason. |
| `400 value_invalid` | `flag` value other than 0 or 1, or `seats` 0. |
| `404 subscription_not_found` / `plan_not_found` / `limit_unknown` / `override_not_found` | As named. |
| `409 plan_inactive` / `too_many_overrides` | Assigning a deactivated plan / the 101st override. |

- "Why does Acme have 75,000?": `GET .../entitlements` shows the source; `GET /api/auth/admin/entitlement-history?tenantId=<id>` shows who and why.
- "maxSeats and seats disagree": only possible for a product without a `seats` limit, where `maxSeats` is the product's own number.

### Decisions and open questions

- See decision 0014. **Open:** a plan edit with thousands of tenants recomputes them all inside the request; the history has no retention job.

---

## Entitlement events

### What it does

ENT-04 (epic AUTH-277). Products are told a tenant's entitlements by webhook, with the same outbox, signing, headers and retries as the other
events (see "Webhooks to products and webhook secret rotation"). Two events carry them, both with `schemaVersion: 1` (`WebhookEvents.SchemaVersion`):

- **`EntitlementsChanged`** (`WebhookEvents.EntitlementsChanged`): one per subscription each time its version rises (`EntitlementState.CommitAsync`
  with `sendEvents` true, the default). Fields: `eventId`, `eventType`, `occurredAt`, `schemaVersion`, `tenantId`, `applicationId`, `planKey`,
  `planName`, `version`, `entitlements` (the complete effective set, null = unlimited), `overridden` (keys).
- **`ApplicationSubscribed`**: the existing fields plus `schemaVersion`, `planKey`, `planName`, `version`, `entitlements`, `overridden`. `maxSeats` is
  now the subscription's effective seat limit after subscribing. Subscribing commits with `sendEvents: false`, so no `EntitlementsChanged` goes
  out for the same change.

Rules:

- Written in the same transaction as the change (`EnqueueChangedEventsAsync` saves them with the caller's context), so an event exists if and
  only if the change does. A plan edit writes one row per changed tenant inside the admin's request; delivery stays with the background worker.
- Payload built by `EntitlementState.Payload` (`EntitlementPayload`), plan key and name by `EntitlementState.PlanNamesAsync`.
- Subscribing accepts `planKey` (`SubscribeRequest.PlanKey`, `AdminSubscribeRequest.PlanKey`): lowercased, must be an active plan of the product
  (`404 plan_not_found`, `409 plan_inactive`, checked before anything is written). Assigning it on subscribe sets `plans.ever_assigned` and writes a
  `plan_assigned` history row. The response (`SubscriptionResponse`) then has `planKey`, `entitlementsVersion` and `entitlements`.
- A subscription from before ENT-03 had version 0 until migration `BackfillEntitlementChangeNumbers` (ENT-05) gave it version 1.

### How it is implemented

| Where | What |
|---|---|
| `EntitlementState.CommitAsync` | Raises versions, then `EnqueueChangedEventsAsync` adds one outbox row per changed subscription and saves. |
| `SubscribeTenantService.SubscribeAsync` | Checks the plan, assigns it, commits entitlements without events, then writes `ApplicationSubscribed` with the snapshot. |
| `SubscriptionEntitlementService.ApplyOverrideAsync` | Takes `sendEvents`, false when called while subscribing. |
| `SubscriptionEndpoints.PlanNotFound` / `PlanInactive` | The two new refusals, shared with the admin subscribe endpoint. |

### Failure modes and production debugging

- "The product did not hear about a plan change": `SELECT event_type, status, attempts, last_error FROM webhook_outbox WHERE application_id = '<id>'
  ORDER BY created_at DESC LIMIT 20;` (the same as any other event; a product with no callback registered gets nothing delivered).
- "The product holds an old number": compare its stored version with `subscriptions.entitlements_version`; a product must apply only higher versions.

### Decisions and open questions

- `ApplicationSubscribed` carries the entitlements instead of a second event, so a new tenant arrives complete in one message.
- **Open:** a plan with thousands of tenants writes thousands of outbox rows in one request; delivery is paced by the worker, not throttled per product.

---

## Entitlement reads

### What it does

ENT-05 (epic AUTH-277, decision 0015). Reading entitlements instead of waiting for an event. A product reads one tenant, or all its tenants in
the order their entitlements changed (first sync, and catching up after missed events). A tenant admin reads their own plan and limits.

Rules (`EntitlementReadEndpoints`):

- **Product, one tenant** (`ProductTenantAsync`): the application is the token's own (`GetApplicationId`); any status is returned, with `status`;
  `404 subscription_not_found` when no subscription exists. Shape `TenantEntitlements`: the event's fields (built with `EntitlementState.Payload`)
  plus `status` and `changeNumber` (`subscriptions.entitlements_change_seq`).
- **Product, all tenants** (`ProductFeedAsync`): `entitlements_change_seq > after` (default 0), ordered by it, `limit` 1 to 500, default 100
  (`EntitlementFeedQuery`, `400` otherwise). Response `PagedResponse<TenantEntitlements, ChangeCursor>`; `next.after` is the last item's number, null on
  the last page. Status changes do not move a subscription in this order (they do not change entitlements).
- **Tenant admin** (`TenantAsync`): `subscriptions.read`, tenant from `tid` only. `TenantOwnEntitlements` with `TenantLimitItem`s (label, description,
  unit, value, overridden); override reasons are never included. The built-in application and products never subscribed: `404 subscription_not_found`.
- **No skipped changes:** `EntitlementState.CommitAsync` locks the rows (`SELECT ... FOR UPDATE`, ordered by id), then takes
  `pg_advisory_xact_lock(hashtextextended('entitlement-change:<applicationId>', 0))` (`ChangeNumberLockPrefix`), then takes numbers. The lock is held to
  commit, so one product's numbers become visible in order.
- Migration `BackfillEntitlementChangeNumbers`: subscriptions still at change number 0 get version 1 and a number. No events.

### How it is implemented

| Verb and route | Auth required | Handler | What it does |
|---|---|---|---|
| `GET /api/auth/tenants/{tenantId}/subscriptions/me/entitlements` | service token | `EntitlementReadEndpoints.ProductTenantAsync` | `200 TenantEntitlements`, `404`. |
| `GET /api/auth/applications/me/entitlements` | service token | `EntitlementReadEndpoints.ProductFeedAsync` | `{items,next}` by change number, `400` for bad paging. |
| `GET /api/auth/tenants/me/subscriptions/{applicationId}/entitlements` | tenant token, `subscriptions.read` | `EntitlementReadEndpoints.TenantAsync` | `200 TenantOwnEntitlements`, `403`, `404`. |

The list uses the existing index on `(application_id, entitlements_change_seq)`; each page computes its rows with one `EntitlementState.SnapshotAsync`.

### Failure modes and production debugging

- "The product missed a change": compare its stored change number with `SELECT tenant_id, entitlements_version, entitlements_change_seq FROM subscriptions
  WHERE application_id = '<id>' ORDER BY entitlements_change_seq DESC LIMIT 20;`.
- "Entitlement changes to one product are slow": a long plan edit holds the product's change-number lock until it commits; look for a waiting
  `pg_advisory_xact_lock` among the database's active queries.

### Decisions and open questions

- See decision 0015. The page size goes to 500 here (other lists stop at 200), because a first sync reads every tenant.
- **Open:** the list has no "since time" filter; products page by change number only.

---

## Entitlement portal reads

### What it does

ENT-06 (epic AUTH-277). Two platform-admin reads the portal needs beside ENT-01 to ENT-05.

- **Plan preview** (`POST /api/auth/admin/applications/{id}/plans/{planKey}/preview`, `PlanEndpoints.PreviewAsync` -> `PlanService.PreviewAsync`): the
  same body as the replace, validated the same way (`400`, `404 plan_not_found`, `400 limit_unknown`, `400 value_invalid`). Answers `PlanPreviewDto`:
  `subscriptions` on the plan and how many would be `changed`. A subscription changes exactly when the replace would raise its version: its overrides
  are kept (`EffectiveEntitlements.Compute` with the new values and the snapshot's overrides, compared with `EffectiveEntitlements.Differ`). Writes nothing.
- **An organisation's subscriptions** (`GET /api/auth/admin/tenants/{tenantId}/subscriptions`, `SubscriptionEntitlementEndpoints.ListForTenantAsync`): every
  status, built-in product left out, paged by product name (`PageQuery.ByNameAsync`). Items `AdminTenantSubscriptionItem`: product id, key and name,
  status, plan key and name, `maxSeats`, `entitlementsVersion`, created. `404 tenant_not_found` for an unknown organisation.

### Failure modes and production debugging

- "The preview said 12 but 13 changed": a tenant subscribed to the plan, or an override changed, between the preview and the save. The preview is advice,
  not a lock.

---

## Product credentials and the service token

### What it does

A product proves who it is to the Auth Service with a **client id** and a **client secret** (OAuth 2.0 client-credentials style) and gets
back a short-lived **service token**. With that token the product subscribes tenants to itself, sets seat limits, suspends or cancels subscriptions,
registers its webhook, manages API keys, and reads its own record. A platform admin can **rotate** the secret; the old secret keeps working
for a grace period so the product can be updated without downtime.

Rules:

- **Endpoint:** `POST /api/auth/token`, anonymous, body as form (`application/x-www-form-urlencoded`) or JSON, fields `grant_type`,
  `client_id`, `client_secret`. Only `grant_type=client_credentials` is accepted.
- **The one string (ONB-03):** registration and rotation also return `credential`, the client id and the secret joined by a dot
  (`cl_<id>.<secret>`; neither part can contain a dot, so the first dot is the join: `ClientCredential`). The endpoint accepts it as
  `client_secret` on its own, or beside its own `client_id`. A `client_id` that names another client leaves the string whole, and it
  fails as `401 invalid_client` like any wrong secret. A plain secret with no `client_id`, or a missing field, is `400 invalid_request`.
  The old `client_id` plus `client_secret` pair keeps working, so nothing already deployed changes. The console shows the credential, not the
  bare secret, as the client secret.
- **Reply:** `{"access_token", "token_type": "Bearer", "expires_in": 600}` with `Cache-Control: no-store` and `Pragma: no-cache`.
  `expires_in` is `Auth:Tokens:ServiceTokenMinutes` (default 10) times 60.
- **Service token claims** (`IdentityTokenService.CreateServiceToken`): `iss` = `Auth:Tokens:Issuer`, `aud` = `auth-service`, `sub` = `app:<applicationId>`,
  `jti`, `token_use` = `service`, `app_id`, `client_id` (`ServiceClaims`). It is signed with the current signing key (ES256).
- A service token is accepted only by endpoints with the `ServiceToken` policy (`IdentityAuthentication.ServicePolicyName`): authenticated, claim
  `token_use=service`, claim `aud=auth-service`, and a valid `app_id`. Identity and admin endpoints reject it with 403. Which product is calling comes only from the
  verified token, never from the request.
- **Secrets:** several can exist at once. A secret works while `expires_at` is null or in the future. Only hashes are stored.
- **Rotation:** `POST /api/auth/admin/applications/{id}/secret/rotate`, optional body `{"graceHours": n}`, `n` 0 to 720
  (`SecretRotationService.MaxGraceHours = 24 * 30`); default `Auth:Applications:SecretGraceHours` (24). Every secret that would live longer than the grace
  period is given `expires_at = now + grace`; one already due to expire sooner keeps its earlier time, so rotating again never extends an
  old secret's life. A new secret with no expiry is added. `graceHours: 0` cuts the old secret off at once (for a leak). Response:
  `applicationId`, `clientId`, `clientSecret` (once), `previousSecretsExpireAt`; `Cache-Control: no-store`.
- **Rate limit:** per client IP address across all products, `Auth:Applications:TokenIpLimit` requests (default 60) per
  `Auth:Applications:TokenIpWindowMinutes` (default 1). Every request counts, valid or not.
- A product that is still **inactive** can get a token (it must be able to call the Auth Service before it is offered to tenants).
  The token endpoint does not check `is_active`.
- A token already issued stays valid until it expires (up to 10 minutes) even after its secret is rotated out or expired.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/token` | none (rate limited, policy `service-token`) | `ServiceTokenEndpoints.TokenAsync` | `ServiceTokenService.IssueAsync` | Checks credentials, issues the service token. |
| `POST /api/auth/admin/applications/{applicationId}/secret/rotate` | platform admin | `ApplicationAdminEndpoints.RotateSecretAsync` | `SecretRotationService.RotateAsync` | Replaces the secret with a grace period. |
| `GET /api/auth/applications/me` | service token | `CurrentApplicationEndpoints.GetAsync` | direct query | The calling product's own record: `id`, `key`, `name`, `jwtAudience`, `clientId`, `isActive`. `401 unauthorized` if the row is gone. |

Other routes that take a service token (documented in their own sections or parts): `POST /api/auth/tenants/{tenantId}/subscriptions`,
`PATCH .../subscriptions/{applicationId}` and `.../seats` (Subscriptions section), `PUT`/`GET /api/auth/applications/me/webhook` (Webhooks
section), the API-key endpoints that use `caller.GetApplicationId()` (`ApiKeyEndpoints`, another part).

Flow of a token request:

1. `SharedRateLimitMiddleware` runs after authentication and before authorization. It resolves the rule
   (`RateLimitPolicies.Resolve`, case `ServiceTokenEndpoints.RateLimitPolicy`): counter key `service-token|<client IP>`, limit and window from
   `ApplicationCredentialOptions`. `IRateLimitStore.HitAsync` counts it; over the limit gives `429` `rate_limited` with `Retry-After`. If the
   store cannot be reached: `503` `rate_limit_unavailable` with `Retry-After: 5` (never fails open; ADR 0008).
2. `TokenAsync` sets `Cache-Control: no-store`, `Pragma: no-cache`, then `ReadRequestAsync` reads form or JSON. A form field that appears twice is read as missing
   (`Single`). A malformed body is treated as null.
3. Missing body, `client_id` or `client_secret`: `400` `invalid_request`. Then, grant type other than `client_credentials`: `400` `unsupported_grant_type`.
4. `ServiceTokenService.IssueAsync` hashes the secret and runs **one** join query: `applications.client_id = clientId AND application_secrets.secret_hash = hash AND
   (expires_at IS NULL OR expires_at > now)`. No row: logs a warning and returns null; the endpoint answers `401` `invalid_client`.
5. On a match it calls `CreateServiceToken` and returns the token. There is no log line and no audit entry on success.

Flow of rotation: `SecretRotationService.RotateAsync` opens a transaction, locks the product (`LockApplicationAsync`), computes
`expiresAt = now + (graceHours ?? options.SecretGraceHours)`, sets `expires_at` on every secret with `expires_at IS NULL OR expires_at > expiresAt`, inserts the new
hashed secret, records `auth.application.secret_rotated` (detail = key), commits. A `graceHours` outside 0 to 720 is `400`; unknown or built-in product `404 application_not_found`.

Data: `applications.client_id`, `application_secrets` (see registration). Libraries: `Microsoft.IdentityModel.JsonWebTokens` for the token. Background
workers: `RateLimitCleanupWorker` removes expired counters every `Auth:RateLimit:CleanupSeconds` (default 60); if it stops, counters grow but windows still expire on use.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Applications:SecretGraceHours` / `Auth__Applications__SecretGraceHours` | 24 | `ApplicationCredentialOptions` | Default grace of the old secret when the rotation body has none. |
| `Auth:Applications:TokenIpLimit` / `Auth__Applications__TokenIpLimit` | 60 | `ApplicationCredentialOptions` | Token requests per IP per window before 429. |
| `Auth:Applications:TokenIpWindowMinutes` / `Auth__Applications__TokenIpWindowMinutes` | 1 | `ApplicationCredentialOptions` | Window of that limit. |
| `Auth:Tokens:ServiceTokenMinutes` / `Auth__Tokens__ServiceTokenMinutes` | 10 | `IdentityTokenOptions` | Service token lifetime and `expires_in`. |
| `Auth:RateLimit:Store` / `Auth__RateLimit__Store` | `Postgres` | `RateLimitOptions` | `Postgres` shares counters across instances; `Memory` is per process. A typo stops the service at startup. |
| `Auth:Proxy:TrustedProxies` / `Auth__Proxy__TrustedProxies__0` (and `TrustedNetworks`) | unset | `ProxyOptions` | Behind a load balancer without it every caller shares the balancer's IP, so one limit of 60 per minute covers all products. |

The client secret is a credential held by the product and the secret store of whoever runs it; it is not a service setting.

### Failure modes and production debugging

| Caller sees | `code` | When |
|---|---|---|
| `400` | `invalid_request` | Body missing, malformed, wrong content type, or `client_id`/`client_secret` missing or repeated. |
| `400` | `unsupported_grant_type` | `grant_type` missing or not `client_credentials`. |
| `401` | `invalid_client` | Unknown client, wrong secret, or expired secret. One answer for all three (the same single query). |
| `429` | `rate_limited` | Per-IP limit passed; `Retry-After` set. |
| `503` | `rate_limit_unavailable` | Rate-limit store (the database) unreachable. |

- Logged: `A service token request was refused: the client credentials were not valid.` (warning, neither client id nor secret in it). For a
  store failure: `The rate-limit store could not be reached, so a request to a limited endpoint was refused ({Policy}).` (error).
- Audit: rotation records `auth.application.secret_rotated`. Issuing a token is not audited, so there is no record of which product asked when.
- Edge cases: a product using the old secret works until `previousSecretsExpireAt` and then gets 401 `invalid_client` with no hint. A
  rotation with `graceHours: 0` makes the old secret fail from the same instant (`expires_at > now` is false at `now`). Whitespace pasted
  around the secret changes its hash and gives 401.
- Likely causes and checks:
  - **Product suddenly cannot get a token (401):** rotated secret expired. `SELECT created_at, expires_at FROM application_secrets WHERE application_id = '<id>' ORDER BY created_at;`
    A row with `expires_at` null is the current one; confirm the product holds that secret (compare hashes only, never log the secret).
  - **Wrong client id:** `SELECT client_id FROM applications WHERE key = '<key>';` (`cl_...`).
  - **429 for many products at once:** they share an egress IP, or the proxy is not listed in `Auth:Proxy`. Check `Auth__Proxy__TrustedProxies__0` and
    the counters. The table `rate_limit_counters` holds only a hash of the key (`key_hash`, `hits`, `expires_at`), so you cannot filter by IP;
    `SELECT hits, expires_at FROM rate_limit_counters ORDER BY hits DESC LIMIT 5;` shows whether one counter is at the limit (60 by default) inside its window.
  - **503 on every token call:** database or migration problem (`rate_limit_counters` missing means the latest migration was not applied; `/health/ready` is 503).
  - **400 `invalid_request` from a product using JSON:** it sent the wrong `Content-Type`; `HasJsonContentType` and `HasFormContentType` are both false.
  - **Service token rejected at an endpoint (403):** the endpoint is not a service-token endpoint, or the product sent the token to an admin/identity endpoint.

### Decisions and open questions

- One query for every failure outcome, so an unknown client and a wrong secret cost the same and the reply reveals nothing about which.
- The service token has the Auth Service's own audience and a non-user `sub`, so it can never be mistaken for a person or a product token.
- A rotated-out secret keeps working for a grace period (default 24 hours), so a product can be updated after the rotation, not before.
- **Open:** successful token issuance is neither logged nor audited. If a leaked secret is suspected there is no trail of use.
- **Open:** the rate limit is per IP only, not per client id, so one noisy product behind a shared egress IP can starve its neighbours.
- **Open:** secrets are compared by hash lookup in the database, not by a fixed-time comparison in code. With 256-bit random secrets this is
  not practically attackable, but I note it for review.
- **Assumption (confirm):** the expired `application_secrets` rows are meant to stay as history (nothing deletes them).

---

## Launch URL and the "my products" read

### What it does

When a person chooses a product in the console's picker, the console needs to know where that product lives. The **launch URL** is that
address, set by a platform admin (never by the product). The **"my products"** read tells the signed-in person, for one organisation, which
products they can open and, for the others, why not. The launch address is only revealed for a product they can open, so the picker cannot be
used to learn where other products live.

Launch URL rules (`LaunchUrlValidator`):

- At most 2000 characters (`MaxLength`, after trimming).
- An absolute URL with scheme `https`. Plain `http` is accepted only for this machine (`localhost`, loopback addresses), for development.
- No user name or password. No `#` fragment.
- Stored as `Uri.AbsoluteUri` (so it is normalised; a bare host gets a trailing `/`).
- A blank or null `url` **clears** the address. A product with no address cannot be opened from the picker.
- Unlike a webhook address it is **not** checked for private addresses, because only a person's own browser follows it.

"My products" rules (`MyApplicationsEndpoints`):

- Needs an identity token and `tenantId` in the query. The caller must be an **Active** member of that organisation, and the organisation
  must not be suspended.
- Lists every **active, non-built-in** product by name, each with a `state`:

| `state` | Meaning (computed in `ListAsync`) |
|---|---|
| `Available` | Subscription is Active and this person has a row in `application_access`. `launchUrl` is given (may still be null if never set). |
| `NoAccess` | Subscription Active, no seat for this person. |
| `NotSubscribed` | The organisation has no subscription row. |
| `SubscriptionSuspended` / `SubscriptionCancelled` | The subscription has that status, even if the person has a seat. |

- `launchUrl` is null for every state except `Available`. The response has `Cache-Control: no-store`.
- A separate read, `GET /api/auth/applications` (identity token, any signed-in person, no tenant), lists the active non-built-in products as
  `{id, key, name}` for the "connect a product" screen.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `PUT /api/auth/admin/applications/{applicationId}/launch-url` | platform admin | `ApplicationAdminEndpoints.SetLaunchUrlAsync` | `LaunchUrlValidator.TryValidate` | Sets or clears `applications.launch_url`; body `{"url": "..."}` or `{"url": null}`. `200 {launchUrl}`. |
| `GET /api/auth/users/me/applications?tenantId={id}` | identity token (policy `IdentityToken`) | `MyApplicationsEndpoints.ListAsync` | one projection query | The picker's data, paged by name. |
| `GET /api/auth/applications` | identity token | `ListApplicationsEndpoints.ListAsync` | one query | Connectable products, paged by name. |

Flow of the launch URL change: validate (a non-blank value that fails gives `400` validation problem on field `Url` with the messages
"Url must be at most 2000 characters.", "Url must be a full web address starting with https://.", "Url must use https. Plain http is only accepted for localhost.",
"Url must not contain a user name or password.", "Url must not contain a # fragment."); load the product with `!IsBuiltIn` (else `404 application_not_found`); set
`LaunchUrl`; record `auth.application.launch_url_set` (detail = key, also when clearing); save.

Flow of "my products": `page.Problem` (bad `limit` or half a cursor: `400`); no user id in the token: `401 unauthorized`; no `tenantId`: `400`
validation problem; one query joins `tenant_memberships` and `tenants` for this user and organisation with status `Active`: none means `403 tenant_access_denied`,
suspended organisation means `403 tenant_suspended`; then one query over `applications` with sub-selects for the tenant's `subscriptions.status` and for
`application_access` for this membership; the state switch yields the item; `Cache-Control: no-store`.

How the console uses it (`src/AuthService.Web`): `ChooseProduct.razor` marks a product openable only when `State == Available` and the address is not blank
(`CanOpen`); otherwise it shows a reason ("You don't have a seat...", "... hasn't subscribed to ...", "The subscription to ... is suspended...", "... has ended.", and for
Available without address "It isn't ready to open yet. Ask the platform administrator to set where it opens."). `POST /choose-product/open`
(`ProductEndpoints.OpenAsync`) re-reads `GET /api/auth/users/me/applications` itself, takes the address from that answer (never from the
request), does a fresh `POST /api/auth/token/exchange` and returns a page that posts `access_token`, `token_type`, `expires_in` as a form body to the
launch address (ADR 0007). The product must therefore accept a `POST` of `application/x-www-form-urlencoded` at its launch address.

Data: `applications.launch_url` (max 2000, migration `AddApplicationLaunchUrl`), `subscriptions`, `application_access`, `tenant_memberships`, `tenants`.
Background workers: none.

### Configuration and environment

None for these routes. Paging limits are fixed (`PageQuery.DefaultLimit = 50`, `MaxLimit = 200`). The picker hand-off needs the console to be able to
reach the launch address with a form post (a `Content-Security-Policy` `form-action` is set for that one response).

### Failure modes and production debugging

| Caller sees | `code` | When |
|---|---|---|
| `400` validation problem on `Url` | none | Launch URL breaks a rule above. |
| `404` | `application_not_found` | Unknown product, or built-in. |
| `400` | none (validation) | `tenantId` missing or `limit` outside 1 to 200. |
| `401` | `unauthorized` | No or invalid token. |
| `403` | `tenant_access_denied` | Not an Active member (Invited, Disabled, not a member, or another person's organisation). |
| `403` | `tenant_suspended` | Active member of a suspended organisation. |
| `403` (empty) | none | Service or product token on an identity endpoint. |

- Audit: `auth.application.launch_url_set`. The reads are not audited.
- Edge cases:
  - A product disappears from the picker entirely when it is withdrawn (`is_active = false`), even if the organisation is subscribed.
  - A product shows but "isn't ready to open yet": `Available` with `launch_url IS NULL`.
  - After a seat is revoked or a subscription is suspended, the picker changes at once, but a product token already issued still works until it expires (15 minutes).
  - A launch address on `http://localhost` works only for development; in production use `https`.
- Likely causes and checks:
  - **Product missing from the picker:** `SELECT key, is_active, is_built_in FROM applications WHERE key = '<key>';` Must be active and not built in. Also check
    paging: the console reads all pages (`GetAllAsync`, 200 per page, up to 50 pages).
  - **Product shown but greyed out:** compute the state by hand:
    `SELECT s.status, (SELECT count(*) FROM application_access a WHERE a.tenant_id = s.tenant_id AND a.application_id = s.application_id AND a.membership_id = '<membershipId>') AS has_seat FROM subscriptions s WHERE s.tenant_id = '<tenantId>' AND s.application_id = '<appId>';`
  - **Product opens a wrong page:** `SELECT launch_url FROM applications WHERE key = '<key>';`.
  - **Product never receives the token:** its launch address does not accept a form `POST`, or a proxy blocks it; the console's hand-off page redirects back to the picker with `error=` codes (`not_available`, `tenant_suspended`, `rate_limited`, `expired`, `unavailable`).

### Decisions and open questions

- The address is set by the platform admin only, so a product cannot redirect people to a place the platform did not approve.
- `launchUrl` appears only when `Available`, so the read cannot be used to discover other products' addresses.
- The token goes in a form body, not in a URL, to keep it out of history, logs and referrers (ADR `docs/decisions/0007-hand-the-token-to-a-product-with-a-form-post.md`).
- **Open:** there is no re-check that the launch host is the same organisation as the product's webhook or audience; an admin can point a launch URL anywhere on https.
- **Open:** state `Available` is true for a person with a seat whose membership is Active, even if the product's subscription has `max_seats` below usage (by design, SUB-05).

---

## Subscriptions

### What it does

A tenant gets to use a product through a **subscription**. It is the **product** that subscribes a tenant to itself, with its service token,
typically from its own onboarding or checkout flow. Tenant people cannot subscribe themselves (403), and there is no admin route to do it for them. The product
also sets the **seat limit**, and can **suspend** or **cancel** the subscription. Individual people then need **access** (a seat) to use the product. Tenant
admins see their subscriptions in a list.

Rules:

- **Status** (`SubscriptionStatus`): `Active`, `Suspended`, `Cancelled`. Stored as text in `subscriptions.status`.
- **One row per tenant and product** (unique index `ux_tenant_application_subscriptions_tenant_id_application_id`). Subscribing again reuses the row.
- **Subscribe** (`POST /api/auth/tenants/{tenantId}/subscriptions`, body optional `{"maxSeats": n}`):
  - `maxSeats` must be at least 1, or left out for unlimited (DB check `ck_tenant_application_subscriptions_max_seats`).
  - Product not active: `400 application_not_active`. Unknown tenant: `404 tenant_not_found`. Suspended tenant: `409 tenant_suspended`.
  - Already Active: idempotent `200`, no write, no event, seat limit unchanged.
  - New: `201`. Suspended or Cancelled and subscribing again: `200`, Active again on the limit sent now, and a new event.
  - Every tenant **Owner** (non-disabled membership with the built-in Owner role) is given access at once and counts as a seat.
- **Suspend or cancel** (`PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}`, body `{"status": "Suspended"|"Cancelled"}`, case-insensitive):
  anything else, including `Active` or a number, is `400`. Same status again is a no-op `200`. Suspended can become Cancelled.
  **Cancelled cannot become Suspended** (`409 subscription_cancelled`). Becoming Active again is subscribing again.
- **Seat limit** (`PATCH .../subscriptions/{applicationId}/seats`, body must contain `maxSeats`: whole number at least 1, or `null`): lowering below current
  usage is accepted; people with access keep it, only new grants are blocked. Missing field, 0, fraction, string: `400`.
- The `{applicationId}` in the two PATCH routes must be the **caller's own**: another product's subscription, a tenant that never subscribed and an unknown tenant all
  give the same `404 subscription_not_found`.
- **Seats used** = rows in `application_access` for the tenant and product whose membership is `Invited` or `Active` (not `Disabled`).

What each status blocks:

| Thing | Active | Suspended | Cancelled |
|---|---|---|---|
| Token exchange for the product (`TokenExchangeService`) | allowed with a seat | `403 exchange_denied` | `403 exchange_denied` |
| Granting a seat (`PUT /api/auth/tenants/me/members/{id}/applications/{appId}`) | allowed while seats are free | `400 application_not_subscribed` | `400 application_not_subscribed` |
| Revoking a seat | allowed | allowed | allowed |
| Seat limit change by the product | allowed | allowed | allowed |
| Picker `state` | `Available` / `NoAccess` | `SubscriptionSuspended` | `SubscriptionCancelled` |
| Seats kept | yes | yes (so reinstating restores everyone) | yes |
| Appears in the tenant's subscription list | yes | yes | yes |

Tokens already issued are **not** revoked: a product token lives `Auth:Tokens:ProductTokenMinutes` (default 15). A product that must cut access sooner checks the
subscription itself. A **suspended tenant** (not subscription) is blocked at token exchange (`403 tenant_suspended` for its Active members) and cannot subscribe.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/tenants/{tenantId}/subscriptions` | service token | `SubscriptionEndpoints.SubscribeAsync` | `SubscribeTenantService.SubscribeAsync` | Subscribes or re-activates; queues `ApplicationSubscribed`. |
| `POST /api/auth/admin/applications/{applicationId}/subscriptions` | platform admin | `ApplicationSubscriptionAdminEndpoints.SubscribeAsync` | `SubscribeTenantService.SubscribeAsync` (actor = the admin) | Body `{tenantId, maxSeats?}`. Same results and event as the product's own call (ONB-02). The built-in product and an unknown id are `404 application_not_found`. |
| `POST /api/auth/admin/applications/{applicationId}/test-organisations` | platform admin | `ApplicationSubscriptionAdminEndpoints.CreateTestOrganisationAsync` | `TenantProvisioning.AddTenantWithOwner`, then `SubscribeTenantService.SubscribeAsync` | Body `{name, ownerEmail, maxSeats?}`. Creates an organisation owned by an existing verified person and subscribes it; `201` with `tenantId`, `tenantName`, `ownerEmail`, `subscription`. |
| `PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}` | service token | `SubscriptionEndpoints.SetStatusAsync` | `SubscriptionStatusService.SetAsync` | Suspend or cancel; queues `ApplicationUnsubscribed`. |
| `PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}/seats` | service token | `SubscriptionEndpoints.SetSeatLimitAsync` | `SeatLimitService.SetAsync` | Sets `max_seats`. |
| `GET /api/auth/tenants/me/subscriptions` | tenant-admin token with `subscriptions.read` at ReadOnly | `ListSubscriptionsEndpoints.ListAsync` | one query | The caller's tenant's subscriptions, all statuses, by product name. |
| `PUT` / `DELETE /api/auth/tenants/me/members/{membershipId}/applications/{applicationId}` | tenant-admin token with `access.manage` at Full | `MemberAccessEndpoints.GrantAsync` / `RevokeAsync` | `MemberAccessService` | Gives or takes away a seat. |

Flow of subscribe (`SubscribeTenantService.SubscribeAsync`):

1. Transaction; `db.LockSubscriptionAsync` takes `pg_advisory_xact_lock(hashtextextended('subscription:<tenant>:<app>', 0))`, so retries for the same pair take turns.
2. Product must exist and be active, else `ApplicationNotActive`. Tenant must exist (`TenantNotFound`) and not be `Suspended` (`TenantSuspended`).
3. Load the existing row. `Active`: return `AlreadySubscribed` (nothing written). Otherwise create or reuse the row, set `Status = Active`, `MaxSeats`, `UpdatedAt`.
4. `GrantOwnersAccessAsync` adds `application_access` rows for Owner memberships that lack one. This step does **not** check the seat limit.
5. Adds the `webhook_outbox` row via `WebhookEvents.Create(... "ApplicationSubscribed" ...)` in the **same transaction**, so the event exists if and only if the change does.
6. Audit `auth.subscription.subscribed` (`AuditActor.Product(applicationId)` by default, or the admin when a platform admin subscribes the tenant, tenant id, detail = product key). Save, commit, answer `201` (new) or `200`.

Flow of a test organisation (`CreateTestOrganisationAsync`, ONB-02): validate `Name` (trimmed, at most `SignupValidator.MaxTenantNameLength`), `OwnerEmail`
(normalised like signup) and `MaxSeats` (at least 1) with `400` on `Name`, `OwnerEmail` or `MaxSeats`; the product must exist, not be built in and be active
(`application_not_found`, `application_not_active`); the owner must exist (`owner_not_found`), have a verified email (`owner_email_not_verified`) and not be
disabled in every membership (`owner_disabled`, `AccountAccess.IsDisabled`). Then `AddTenantWithOwner` and the audit entry `auth.tenant.created` (actor = the admin)
are saved, and `SubscribeAsync` runs as above. The two saves are separate: if the product were withdrawn between the check and the subscribe, the organisation exists
without a subscription and the answer is `409 organisation_not_subscribed`.

Flow of status change (`SubscriptionStatusService.SetAsync`): lock as above; not found, `404 subscription_not_found`; same status, `Unchanged`; current `Cancelled`, `AlreadyCancelled`;
otherwise update the status, add the `ApplicationUnsubscribed` outbox row (payload has `status`), audit `auth.subscription.suspended` or `auth.subscription.cancelled`, commit.
Flow of seat limit (`SeatLimitService.SetAsync`): find the row (no advisory lock), set `MaxSeats`, audit `auth.subscription.seat_limit_changed`, save.

Granting a seat (`MemberAccessService.GrantAsync`): membership must belong to the caller's tenant (`404 not_found` otherwise, never 403); `SELECT * FROM subscriptions ... FOR UPDATE`;
not an Active subscription: `400 application_not_subscribed`; membership Disabled: `409 membership_disabled`; already has access: `200`; `seatsUsed >= maxSeats`:
`409 no_seat_available`; else insert `application_access`, audit `auth.membership.access_granted` (detail `membershipId.productKey`), `201`.
The same seat check also gates two other flows: inviting a person with product access (`InviteUserService`) and re-enabling a Disabled member (`MembersService`), both `409 no_seat_available`.

Data:

| Table | Entity | Key columns |
|---|---|---|
| `subscriptions` | `TenantApplicationSubscription` | `tenant_id`, `application_id`, `status` (text), `max_seats` (null = unlimited), `created_at`, `updated_at` |
| `application_access` | `MembershipApplicationAccess` | `tenant_id`, `membership_id`, `application_id`, `granted_at`; unique on (`membership_id`, `application_id`); FK to `tenant_memberships` on (`tenant_id`, `membership_id`) |
| `webhook_outbox` | `WebhookOutboxMessage` | see the Webhooks section |

State transitions: new -> Active; Active -> Suspended; Active or Suspended -> Cancelled; Suspended or Cancelled -> Active (only by subscribing again). Cancelled -> Suspended is refused.

Libraries: EF Core with raw SQL for locks. Background workers: the webhook dispatcher delivers the events (next section).

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Webhooks:RetryWindowHours` / `Auth__Webhooks__RetryWindowHours` | 24 | `WebhookOptions` | The `deadline_at` stamped on each new event (subscribe and status change). |
| `Auth:Tokens:ProductTokenMinutes` / `Auth__Tokens__ProductTokenMinutes` | 15 | `IdentityTokenOptions` | How long a token survives a suspension or revoked seat. |

No other settings; seat limits are data, set by the product.

### Failure modes and production debugging

| Caller sees | `code` | When |
|---|---|---|
| `400` | `application_not_active` | Subscribe to a withdrawn or never-activated product. |
| `404` | `tenant_not_found` | Subscribe for an unknown tenant. |
| `409` | `tenant_suspended` | Subscribe for a suspended tenant. |
| `400` validation | none | `maxSeats` below 1; bad `status`; bad seat body (field `maxSeats` or `Status`). |
| `404` | `subscription_not_found` | PATCH on another product's subscription, a tenant that never subscribed, or an unknown tenant. |
| `409` | `subscription_cancelled` | Suspending a Cancelled subscription. |
| `400` / `409` | `application_not_subscribed` / `no_seat_available` / `membership_disabled` | Seat grants (see above). |
| `403` / `401` | none / `unauthorized` | Wrong kind of token / none. A tenant user, admin or product token on a service route gets an empty 403. |

- Audit actions: `auth.subscription.subscribed`, `auth.subscription.suspended`, `auth.subscription.cancelled`, `auth.subscription.seat_limit_changed`,
  `auth.membership.access_granted`, `auth.membership.access_revoked`.
- Edge cases and half-states:
  - **Owners bypass the seat limit on subscribe.** With `maxSeats: 1` and two Owners, both get access and `seatsUsed` is 2 (above the limit); further grants are then blocked.
  - **Seats exceed the limit after lowering it.** People keep access; `GET .../subscriptions` shows `seatsUsed` above `maxSeats`.
  - **Re-subscribing a Cancelled tenant** restores everyone's old seats at once (their `application_access` rows were kept).
  - **A suspended subscription still has events pending:** a subscribe event that failed delivery may still be retried after the status changed.
  - An event for `ApplicationSubscribed` is queued even when the product has registered no callback yet; it waits (see Webhooks).
- Likely production issues and checks:
  - **A tenant cannot open a product:** go through the exchange conditions in order. (1) membership Active:
    `SELECT status FROM tenant_memberships WHERE tenant_id = '<t>' AND user_id = '<u>';` (2) tenant not suspended: `SELECT status FROM tenants WHERE id = '<t>';`
    (3) product active: `SELECT is_active FROM applications WHERE id = '<a>';` (4) subscription Active:
    `SELECT status, max_seats, updated_at FROM subscriptions WHERE tenant_id = '<t>' AND application_id = '<a>';` (5) seat row:
    `SELECT granted_at FROM application_access WHERE tenant_id = '<t>' AND application_id = '<a>' AND membership_id = '<m>';`. Any miss is the same `403 exchange_denied`
    (except a suspended tenant, which says `tenant_suspended`).
  - **Seats "full":** `SELECT s.max_seats, count(*) FILTER (WHERE m.status <> 'Disabled') AS used, count(*) FILTER (WHERE m.status = 'Invited') AS invited FROM subscriptions s JOIN application_access a ON a.tenant_id = s.tenant_id AND a.application_id = s.application_id JOIN tenant_memberships m ON m.id = a.membership_id WHERE s.tenant_id = '<t>' AND s.application_id = '<a>' GROUP BY s.max_seats;`
    Invited people (not yet accepted, and not expired) use a seat; Disabled ones and expired invitations do not. The query above still counts
    expired invitations; the API leaves them out (`SubscriptionResponses.SeatHolders`, part 2).
  - **The product says it subscribed a tenant but nothing happened:** the product probably received `400 application_not_active` or `409 tenant_suspended`; or it sent the call with
    another product's service token (the product is always the token's).
  - **Product was not told:** look at `webhook_outbox` (next section).

### Decisions and open questions

- Billing stays in the product; only the seat number crosses the boundary.
- Access (seat) and roles are separate: a subscription alone lets nobody in, and roles alone do not either.
- Suspension keeps seats so reinstating is a single call; becoming Active again is "subscribe again" so there is one way back.
- Tokens are not revoked on suspension (short lifetime instead); this is documented as by design in the README.
- **Inconsistent/risky:** the Owner auto-grant ignores `maxSeats` (can exceed it on day one). Also `SeatLimitService.SetAsync` does not take the advisory lock that subscribe and status
  change use; concurrent writes are still ordered by row locks, but I did not find a test that covers a seat-limit change racing a subscribe.
- **Open:** no endpoint lets a platform admin or tenant admin subscribe or cancel on a product's behalf; if a product is gone, its subscriptions can only be changed with SQL.
- **Open:** the tenant sees its subscriptions but cannot cancel one; only the product can.
- **Assumption (confirm):** "typically from its own onboarding or checkout flow" (README) means every product implements its own call to `POST .../subscriptions`; no shipped helper does it (I found none in `src/AuthService.Authorization` or `samples`).

---

## Webhooks to products and webhook secret rotation

### What it does

When a tenant is subscribed to a product, or its subscription is suspended or cancelled, the Auth Service tells the product by sending a **signed HTTP POST** to the
product's own callback address. Events are written to a database queue (the **outbox**) in the same transaction as the change, and a background worker delivers them with
retries for up to 24 hours. A platform admin can see events that could not be delivered and send them again. The product verifies each call with a **signing secret**
that only it and the Auth Service know; the secret can be rotated with an **overlap** so the product can switch without losing events.

Rules:

- **Events** (`WebhookEvents`): `ApplicationSubscribed` and `ApplicationUnsubscribed`. There are no others.
- **Delivery is at-least-once.** A product must drop duplicates by event id.
- **Callback address** (`WebhookUrlValidator`): absolute, at most 2000 characters, scheme `https`, no user name/password, no fragment, not a private target. Rejected as private:
  `localhost`, `*.localhost`, `*.local`, `*.internal`, any host with **no dot** (unless an IPv6 literal), and private/loopback/link-local/multicast/CGNAT/`0.x`/`>=224` IP literals
  (`IsPrivate`). `Auth:Webhooks:AllowPrivateAddresses` (development only) allows plain `http` and private addresses. The address is checked again at connect time (below).
- **Timeouts:** 10 seconds for a response (`Auth:Webhooks:TimeoutSeconds`), 5 seconds to connect (hard-coded `ConnectTimeout`). Redirects are **not** followed and count as failures.
- **Success** is any `2xx`. Anything else (another status, redirect, timeout, connection or TLS error) is a failure.
- **Retries:** delay after each failed attempt is 1 minute, 5 minutes, 30 minutes, 2 hours, 6 hours, then 12 hours repeating (`WebhookOptions.DefaultRetryDelaySeconds = [60, 300, 1800, 7200, 21600, 43200]`),
  never beyond the event's **deadline** (created time + `RetryWindowHours`, 24 hours). One last attempt is made **at** the deadline, then the event is marked `Failed`.
  Counting from the first attempt with instant failures that is attempts at about 0, 1 min, 6 min, 36 min, 2 h 36 min, 8 h 36 min, 20 h 36 min and 24 h (8 attempts; derived from the
  delays and the cap; `TST04_RetriesTheDeadlineAndAResendAreObservedAgainstTheRealReceiver` asserts the gaps).
- **No callback registered yet:** the event waits (not counted as an attempt), is looked at again every `NoUrlRecheckSeconds` (300), and fails with `last_error` `No callback URL was registered in time.` at the deadline.
- **Resend:** a platform admin can put a `Failed` event back as `Pending` with attempts reset to 0, due now, and a fresh 24-hour deadline.

What the product receives: `POST <callback>` with the JSON body and these headers (set in `WebhookDeliveryService.TrySendAsync`):

| Header | Value |
|---|---|
| `Content-Type` | `application/json; charset=utf-8` (a .NET `StringContent` with UTF-8; tests assert it starts with `application/json`) |
| `User-Agent` | `AuthService-Webhooks/1` |
| `X-Auth-Event-Id` | The outbox id (a GUID). Same on every retry. Equals `eventId` in the body. |
| `X-Auth-Event-Type` | `ApplicationSubscribed` or `ApplicationUnsubscribed`. |
| `X-Auth-Timestamp` | Unix seconds of **this attempt** (not of the event). Changes on every retry. |
| `X-Auth-Signature` | `v1=<hex>`; during a secret overlap two entries separated by a comma, newest secret first: `v1=<hex>,v1=<hex>`. |

Body fields (`SubscribeTenantService`, `SubscriptionStatusService`): `eventId`, `eventType`, `occurredAt` (ISO 8601), `tenantId`, `applicationId`, and `maxSeats` (subscribed; may be null) or
`status` (`Suspended` or `Cancelled`; unsubscribed).

**Signature format** (`WebhookSigner`): `v1=` followed by lowercase hex of `HMAC-SHA256(key, "{timestamp}.{body}")`, where `key` is the **UTF-8 bytes of the whole secret string**
(including the `whsec_` prefix; it is not base64-decoded), `timestamp` is the `X-Auth-Timestamp` text, and `body` is the exact request body.
A secret looks like `whsec_` followed by 43 base64url characters (`WebhookSigner.NewSecret`).

**How a receiver should verify** (the service ships no verification helper; I found none in `src/AuthService.Authorization` or the samples):

1. Read the **raw request body bytes** before any JSON parsing. Do not re-serialise: the body is the text PostgreSQL returns for the `jsonb` column `webhook_outbox.payload_json`, so
   key order and spacing may differ from what you would produce.
2. Read `X-Auth-Timestamp` and reject the call if it is too far from your clock. The service defines no tolerance. **Assumption (confirm):** five minutes is a sensible tolerance, since
   each retry is stamped with its own attempt time.
3. For each secret you hold (new first, old until you have switched), compute `"v1=" + hex_lower(HMAC_SHA256(utf8(secret), timestamp + "." + body))`.
4. Split `X-Auth-Signature` on `,`. Accept if any computed value equals any entry, using a constant-time comparison.
5. Drop the event if you have already processed `X-Auth-Event-Id`.
6. Answer `2xx` quickly (within 10 seconds) with no redirect; do slow work afterwards.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `PUT /api/auth/applications/me/webhook` | service token | `WebhookConfigEndpoints.SetAsync` | `WebhookUrlValidator.TryValidate`, `WebhookSigner.NewSecret`, `KeyEncryption.Encrypt` | Registers the callback; creates or rotates the signing secret. |
| `GET /api/auth/applications/me/webhook` | service token | `WebhookConfigEndpoints.GetAsync` | direct query | `{url, hasSecret, previousSecretExpiresAt}`; the overlap time only while it is in the future. |
| `GET /api/auth/admin/webhooks` | platform admin | `WebhookAdminEndpoints.ListAsync` | one query | Events newest first. Query `status` (Pending, Delivered, Failed), `applicationId`, `limit` (1 to 200, default 50), `before` and `beforeId` (cursor). Response `{items, next:{before, beforeId}}`. |
| `POST /api/auth/admin/webhooks/{webhookId}/resend` | platform admin | `WebhookAdminEndpoints.ResendAsync` | conditional `ExecuteUpdateAsync` | Failed -> Pending, fresh deadline. |

Registration and rotation (`WebhookConfigEndpoints.SetAsync`), body `{"url": "...", "rotateSecret": bool?, "overlapHours": int?}`:

1. Validate the URL (`400` on field `Url`, messages: "Url must be an absolute URL of at most 2000 characters.", "Url must use https.", "Url must not contain credentials or a fragment.",
   "Url must point at a public address."). `overlapHours` outside 0 to 720 (`WebhookOptions.MaxSecretOverlapHours`) is `400` on `OverlapHours`, even when no rotation is requested.
2. Load the calling product (id from the token). A new secret is made if the product has none (first registration) or `rotateSecret` is true.
3. When replacing an existing secret and `overlapHours > 0` (default `Auth:Webhooks:SecretOverlapHours`, 24): the current secret is copied to `webhook_previous_secret_protected` with
   `webhook_previous_secret_expires_at = now + overlap`. Any earlier previous secret is overwritten, so there are **never more than two**. With `overlapHours` 0 the previous secret is cleared: the old one stops signing at once (a leak).
4. The new secret `whsec_...` is encrypted with `KeyEncryption.Encrypt` (AES-256-GCM, key `Auth:Tokens:KeyEncryptionKey`) and stored in `webhook_secret_protected`.
5. Audit: `auth.webhook.secret_rotated` (detail `overlap_hours_<n>`) only when a secret was replaced; `auth.application.webhook_configured` (detail = product key) on every successful call.
6. Answer `200` `{url, signingSecret, previousSecretExpiresAt}` with `Cache-Control: no-store`. `signingSecret` is non-null only when a secret was created or rotated, and is shown once.

Delivery (`WebhookDispatcher` -> `WebhookDeliveryService.DeliverDueAsync`):

1. `WebhookDispatcher` (a `BackgroundService`, registered in `Program.cs`) wakes every `Auth:Webhooks:DispatchIntervalMilliseconds` (default 2000, minimum 10) and calls `DeliverDueAsync`,
   which handles up to 50 events per wake (`BatchLimit`).
2. For each: a transaction and `SELECT * FROM webhook_outbox WHERE status = 'Pending' AND next_attempt_at <= now ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED`.
   Several instances can run; each takes a different event.
3. It reads the product's `webhook_url`, current secret and previous secret. A previous secret whose expiry has passed is **deleted** from the row here (`ExecuteUpdateAsync`) and not used.
4. No URL or no secret: no attempt is made; at or after the deadline the event is `Failed` (`LastError = "No callback URL was registered in time."`, logged), otherwise `next_attempt_at` is moved by 300 seconds (not past the deadline).
5. Otherwise `TrySendAsync` decrypts the secrets, signs with each, and posts using the named HTTP client `webhooks` (`WebhookHttp`): no redirects, 5 s connect timeout, 10 s overall, and a `ConnectCallback` that
   resolves the host **at connect time** and refuses private addresses unless `AllowPrivateAddresses` is on.
6. Result: success -> `Delivered`, `delivered_at` set, `last_error` null. Failure -> `last_error` set to `HTTP <code>`, `Timed out`, or the exception type name (never a URL or a body); if the attempt finished at or after the
   deadline -> `Failed`; else `next_attempt_at = min(finished + delay, deadline)`. `attempts` increases by one for every real attempt.
7. Saved and committed; the row lock is held for the duration of the HTTP call.

Data: `webhook_outbox` (entity `WebhookOutboxMessage`): `id` (the event id), `application_id`, `event_type`, `payload_json` (jsonb), `status` (text: `Pending`, `Delivered`, `Failed`), `attempts`, `next_attempt_at`, `deadline_at`,
`delivered_at`, `last_error` (max 500), `created_at`; index on (`status`, `next_attempt_at`). `applications.webhook_url` (2000), `webhook_secret_protected` (500), `webhook_previous_secret_protected` (500),
`webhook_previous_secret_expires_at`.

Resend (`ResendAsync`): 404 `webhook_not_found` if the id is unknown; then one conditional `UPDATE ... WHERE id = {id} AND status = 'Failed'` setting `Status = Pending`, `Attempts = 0`, `NextAttemptAt = now`, `DeadlineAt = now + RetryWindowHours`;
no row changed: `409 webhook_not_failed`; otherwise audit `auth.webhook.resent` (detail = webhook id) and `200` with the item. Two admins pressing at once queue it once.

Libraries and external services: `HttpClient` via `IHttpClientFactory` with `SocketsHttpHandler`; `System.Security.Cryptography` (`HMACSHA256`, AES-GCM); PostgreSQL row locks; the product's own HTTPS endpoint over the public internet.
Background worker: **`WebhookDispatcher`** (interval 2 s). If it is not running (disabled by `Auth:Webhooks:DispatcherEnabled=false`, or no API instance up) no event is delivered; events stay `Pending` and age; metrics show it (below).

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Webhooks:RetryWindowHours` / `Auth__Webhooks__RetryWindowHours` | 24 | `WebhookOptions` | Deadline stamped on new events and resends. Existing rows keep theirs. |
| `Auth:Webhooks:RetryDelaySeconds` / `Auth__Webhooks__RetryDelaySeconds__0` (indexed) | null = `[60, 300, 1800, 7200, 21600, 43200]` | `WebhookOptions` | Delays by failed-attempt count; the last repeats. The default is in code because array binding appends. |
| `Auth:Webhooks:SecretOverlapHours` / `Auth__Webhooks__SecretOverlapHours` | 24 | `WebhookOptions` | Default overlap when a rotation names none. |
| `Auth:Webhooks:TimeoutSeconds` / `Auth__Webhooks__TimeoutSeconds` | 10 | `WebhookOptions`, `WebhookHttp` | Wait for a response (minimum 1). |
| `Auth:Webhooks:DispatcherEnabled` / `Auth__Webhooks__DispatcherEnabled` | true | `WebhookOptions` | False: the worker logs `The webhook dispatcher is disabled; queued webhooks will wait.` and exits; read once at start. |
| `Auth:Webhooks:DispatchIntervalMilliseconds` / `Auth__Webhooks__DispatchIntervalMilliseconds` | 2000 | `WebhookOptions` | How often the worker polls. |
| `Auth:Webhooks:NoUrlRecheckSeconds` / `Auth__Webhooks__NoUrlRecheckSeconds` | 300 | `WebhookOptions` | Re-check interval for a product with no callback. |
| `Auth:Webhooks:AllowPrivateAddresses` / `Auth__Webhooks__AllowPrivateAddresses` | false | `WebhookOptions` | **Never set in production** (`docs/deployment.md`): allows http and private/loopback targets (server-side request forgery risk). |
| `Auth:Tokens:KeyEncryptionKey` / `Auth__Tokens__KeyEncryptionKey` | none; required outside Development/Testing | `IdentityTokenOptions` | Encrypts the webhook secrets as well as signing keys. Losing or changing it makes every stored webhook secret undecryptable. |
| `ConnectionStrings:AuthDb` / `ConnectionStrings__AuthDb` | none; required | `Program.cs` | The outbox lives here. |

Metrics (`/metrics`, from `ObservabilityEndpoints`): `auth_webhook_outbox_messages{status="pending"|"failed"}` and `auth_webhook_outbox_oldest_pending_age_seconds`. `docs/deployment.md` suggests alerting on `failed` above zero.

### Failure modes and production debugging

What a caller sees:

| Caller | Status and `code` | When |
|---|---|---|
| Product, `PUT .../webhook` | `400` validation (`Url` / `OverlapHours`) | Bad URL (http, private, credentials, fragment, too long) or overlap outside 0 to 720. |
| Product | `401` / `403` | No token / wrong kind of token. |
| Admin, `GET /admin/webhooks` | `400` validation (`limit`, `status`) | "Limit must be between 1 and 200." or "Status must be Pending, Delivered or Failed." |
| Admin, resend | `404` `webhook_not_found`; `409` `webhook_not_failed` | Unknown id; the event is Pending or Delivered. |

Logged (all name ids and outcomes only, never a URL or body; `RemoveAllLoggers()` silences the HTTP client's own logging):

- Information: `Webhook {EventId} ({EventType}) delivered after {Attempts} attempt(s).`
- Warning: `Webhook {EventId} ({EventType}) attempt {Attempts} failed, will retry in {DelaySeconds}s: {Error}`
- Error: `Webhook {EventId} ({EventType}) failed for good after {Attempts} attempt(s): {Error}`
- Error: `Webhook {EventId} ({EventType}) failed: its product registered no callback URL in time.`
- Error: `Delivering queued webhooks failed; trying again shortly.` (the loop itself failed, for example the database was unreachable; events stay queued)
- Information at start: `The webhook dispatcher is disabled; queued webhooks will wait.`

Audit: `auth.application.webhook_configured`, `auth.webhook.secret_rotated`, `auth.webhook.resent`. Secrets never reach the logs or the audit log (`SUB07_SecretsStayEncryptedAtRestAreNeverLoggedAndNeverReachTheAuditLog`).

The `last_error` values and their usual meaning:

| `last_error` | Likely meaning |
|---|---|
| `HTTP 4xx` / `HTTP 5xx` | The product answered with an error. `HTTP 401/403` often means a gateway in front of the callback; `HTTP 404` a wrong path; `HTTP 5xx` the product is down. |
| `HTTP 301` / `HTTP 302` / `HTTP 307` | The callback redirects (for example http to https, or a trailing slash). Redirects are never followed. Register the final address. |
| `Timed out` | No response in 10 seconds (`TaskCanceledException`). |
| `HttpRequestException` | Connection refused, DNS failure, **untrusted or expired TLS certificate**, or the address resolved to a private IP (`The callback address is not a public address.`). Details are deliberately not kept. |
| other .NET exception type name | An internal failure while sending. If every event shows a `Cryptographic...` type, suspect `Auth:Tokens:KeyEncryptionKey` (**Assumption (confirm):** `AuthenticationTagMismatchException` is the type thrown by AES-GCM on a wrong key; the code stores `exception.GetType().Name`). |
| `No callback URL was registered in time.` | The product never registered a callback within the deadline. Not an attempt. |

Edge cases and half-states:

- **Order is not guaranteed.** Events are picked oldest first, but a failed older event is retried later, so a newer `ApplicationUnsubscribed` can arrive before an older `ApplicationSubscribed`. Compare `occurredAt` and the current state.
- **Duplicates.** The HTTP call happens inside the database transaction; a crash after the product answered but before commit leaves the event `Pending`, so it is sent again with the same event id (and a new timestamp and signature).
- **The secret used is the one at delivery time**, not at event creation. A rotation affects events already queued.
- **Rotation with `overlapHours` 0 breaks queued events** if the product has not yet switched to the new secret; they will fail verification and be retried until it does.
- **The signature differs on every attempt** because the timestamp is signed. A product that caches "signature seen" instead of the event id will misbehave.
- A callback with a host that has no dot (for example `https://receiver:8443`, a Docker service name) is refused as private at registration.
- A row in `webhook_outbox` has a foreign key to `applications` with restrict delete.
- The connect-time address check still runs when DNS changed after registration: a name later pointed at a private address will fail with `HttpRequestException` and the event stays Pending then Failed.

Likely production issues and how to confirm:

- **Events stay Pending with `attempts = 0` and `next_attempt_at` in the past:** the dispatcher is not running. Check `auth_webhook_outbox_oldest_pending_age_seconds` growing, the log line about the dispatcher being disabled, and that
  at least one API instance is up and `Auth__Webhooks__DispatcherEnabled` is not `false` (also check that a migration/maintenance tool did not start the API with it off).
  `SELECT status, count(*), min(created_at), min(next_attempt_at) FROM webhook_outbox GROUP BY status;`
- **Events Pending with `attempts > 0`:** the product is failing. `SELECT id, application_id, event_type, status, attempts, last_error, next_attempt_at, deadline_at FROM webhook_outbox WHERE status <> 'Delivered' ORDER BY created_at DESC LIMIT 50;`
  or the admin list `GET /api/auth/admin/webhooks?status=Pending`.
- **Events Failed:** the product was down or unreachable for 24 hours, or never registered. Fix the cause, then `POST /api/auth/admin/webhooks/{id}/resend` (also available in the console at `/platform/webhooks`).
- **Product reports a bad signature:** see the symptom table; first confirm the product hashes the raw body and uses the whole `whsec_...` string as the key.
- **Product received nothing after registering:** `SELECT webhook_url, webhook_secret_protected IS NOT NULL AS has_secret, webhook_previous_secret_expires_at FROM applications WHERE key = '<key>';` and the matching `webhook_outbox` rows.
- **Certificate error:** the callback needs a certificate from a publicly trusted authority; CI cannot prove that, so `docs/release-checklist.md` ("Webhook delivery to a real receiver") has a manual check to run before each release (register a real HTTPS receiver, subscribe a test tenant, verify headers and signature, make the
  receiver answer 503 and 302, point a name at 127.0.0.1).

### Decisions and open questions

- **Outbox in the same transaction** so an event exists if and only if the change does (`SUB_TC20_ASubscriptionThatFailsToSaveLeavesNoWebhookBehind`).
- **Signature over `timestamp.body`** so the timestamp cannot be changed to replay an old call; **HMAC with a per-product secret** so a product can verify without a public-key scheme.
- **Secret stored encrypted, not hashed**, because the service must use it (comment on `Application.WebhookSecretProtected`); it shares the key-encryption secret with signing keys.
- **Two secrets at most, in the same header**, so a receiver can accept either while it switches (SUB-07). The oldest is dropped on a second rotation.
- **No redirects and a connect-time address check** to stop a product pointing the service at its own network (SSRF).
- **Inconsistent/risky:** the webhook list `GET /api/auth/admin/webhooks` is also the console's admin probe, so any load problem there looks like "platform unavailable".
- **Inconsistent/risky:** the database row lock and transaction stay open during the HTTP call (up to about 10 seconds, the client timeout); with many slow receivers a single instance handles events one after another (50 per cycle).
- **Open:** there is no per-event or per-product pause, no dead-letter queue besides `Failed`, and `webhook_outbox` rows are never deleted (I found no cleanup code), so the table grows with every subscription change.
- **Open:** no tolerance for timestamps is defined for receivers (the five minutes above is my assumption).
- **Unknown:** whether any product in production already verifies the signature; `samples/SampleProduct*` and `src/AuthService.Authorization` contain no webhook verification (`grep` for `X-Auth-Signature` finds only the service, tests and docs). Ask the owner of each product.
- The console page `/platform/webhooks` (`src/AuthService.Web/Components/Pages/Platform/PlatformWebhooks.razor`) lists events, shows `LastError` cut to 160 characters (`Shorten`), and offers "Send again" for `Failed` ones.

---

## Symptom to where to look (this part)

| Symptom a user or on-call engineer reports | Likely cause | First place to look (file and method, setting, table) | What to check |
|---|---|---|---|
| Webhook stuck `Pending`, `attempts = 0`, due time long past | Dispatcher not running or disabled | `WebhookDispatcher.ExecuteAsync`; `Auth__Webhooks__DispatcherEnabled`; metric `auth_webhook_outbox_oldest_pending_age_seconds` | Log line `The webhook dispatcher is disabled; queued webhooks will wait.`; is an API instance up; `SELECT status, count(*), min(next_attempt_at) FROM webhook_outbox GROUP BY status;` |
| Webhook `Pending`, `attempts` rising, `last_error` `HTTP 5xx` | Product endpoint down or failing | `WebhookDeliveryService.DeliverNextAsync`; `webhook_outbox.last_error` | Product health; next try is `next_attempt_at`; fixes itself on the next success |
| Webhook `Failed` after about 24 hours | Product unreachable for the whole window, or never registered a callback | `webhook_outbox.deadline_at`, `last_error`; `POST /api/auth/admin/webhooks/{id}/resend` | Fix the product, resend; `No callback URL was registered in time.` means `applications.webhook_url IS NULL` |
| `last_error` `HttpRequestException` for every event of one product | Untrusted/expired certificate, DNS failure, or name resolving to a private address | `WebhookHttp.CreateHandler` (`ConnectCallback`); `docs/release-checklist.md` | Open the callback in a browser from outside; `SELECT webhook_url FROM applications WHERE key = '<key>'`; certificate chain and expiry |
| `last_error` `HTTP 301` or `HTTP 302` | Callback redirects; redirects are not followed | `WebhookHttp.CreateHandler` (`AllowAutoRedirect = false`) | Register the final https address with `PUT /api/auth/applications/me/webhook` |
| Product reports "bad signature" | Product hashes a re-serialised body, uses a base64-decoded or truncated secret, compares only one of two entries, or has an old secret | `WebhookSigner.Sign`; README "Verifying during an overlap" | Hash the raw bytes; key = full `whsec_...` text as UTF-8; message = `{X-Auth-Timestamp}.{body}`; compare with every comma-separated entry |
| Bad signature only after the product rotated its webhook secret | Product stored the wrong value or the overlap ended | `webhook_previous_secret_expires_at`; `GET /api/auth/applications/me/webhook` (`previousSecretExpiresAt`); audit `auth.webhook.secret_rotated` detail `overlap_hours_<n>` | Overlap 0 cuts the old secret off at once; the new secret is shown once, so re-rotate to get another |
| Every webhook fails with a cryptographic exception name | `Auth:Tokens:KeyEncryptionKey` changed or lost, so stored secrets cannot be decrypted | `KeyEncryption.Decrypt`; `Auth__Tokens__KeyEncryptionKey` | `last_error` value; compare the key with the one in the secret store; products must re-register (`rotateSecret: true`) after a key change |
| Product cannot get a service token (`401 invalid_client`) | Wrong client id, wrong or expired secret (rotation grace over), whitespace in the secret | `ServiceTokenService.IssueAsync`; `application_secrets.expires_at` | `SELECT created_at, expires_at FROM application_secrets WHERE application_id = '<id>' ORDER BY created_at;`; log `A service token request was refused...` |
| Product gets `400 invalid_request` from `/api/auth/token` | Body not form or JSON, field missing or repeated | `ServiceTokenEndpoints.ReadRequestAsync` | Content-Type header and field names `grant_type`, `client_id`, `client_secret` |
| Product or many products get `429 rate_limited` on `/api/auth/token` | 60 requests per minute per IP exceeded, often because the proxy is not trusted and all callers share one address | `RateLimitPolicies.Resolve`; `Auth__Applications__TokenIpLimit`; `Auth__Proxy__TrustedProxies__0` | `SELECT hits, expires_at FROM rate_limit_counters ORDER BY hits DESC LIMIT 5;` (keys are hashed); `Retry-After` header; cache the service token for its 10 minutes |
| `503 rate_limit_unavailable` on the token endpoint | Database unreachable or latest migration missing | `SharedRateLimitMiddleware`; `/health/ready` | Log `The rate-limit store could not be reached...`; run the migrate job |
| Secret rotation broke a product | Product still uses the old secret after `previousSecretsExpireAt`, or `graceHours: 0` was used | `SecretRotationService.RotateAsync`; `application_secrets` | Expiry times; give the product the new secret; next time use a longer grace |
| Tenant person cannot open a product (`403 exchange_denied`) | No Active membership, product inactive, subscription not Active, or no seat | `TokenExchangeService.ExchangeAsync` | Run the five checks listed in the Subscriptions section |
| Add/invite/enable fails with `409 no_seat_available` ("seats full") | `seatsUsed >= max_seats`; Invited people with an unexpired invitation count, Disabled and expired do not | `MemberAccessService.GrantAsync`; seat counting in `SubscriptionResponses.SeatsUsedAsync` | Seat query in the Subscriptions section; product can raise the limit with `PATCH .../seats` |
| Product does not appear in the console picker | Inactive, built in, or the person's organisation is not what they think | `MyApplicationsEndpoints.ListAsync`; `applications.is_active` | `SELECT key, is_active, is_built_in FROM applications WHERE key = '<key>';` and that the activation had a catalog |
| Product appears but "isn't ready to open yet" | `Available` but `launch_url` is null | `ApplicationAdminEndpoints.SetLaunchUrlAsync`; `applications.launch_url` | Set it with `PUT /api/auth/admin/applications/{id}/launch-url`; must be https |
| Product shows "subscription is suspended" or "has ended" | Status set by the product via `PATCH` | `SubscriptionStatusService.SetAsync`; `subscriptions.status` | Audit entries `auth.subscription.suspended`/`cancelled`; the product subscribes the tenant again to reinstate |
| Product's `POST .../subscriptions` returns `400 application_not_active` | Product withdrawn or never activated | `PermissionCatalogService.SetActiveAsync` | `applications.is_active`; `SELECT count(*) FROM permissions WHERE application_id = ...` |
| Admin gets a blank `403` on the platform console or admin API | Not flagged as platform admin, or using a product/service token | `PlatformAdminHandler`; `users.is_platform_admin` | `SELECT email, is_platform_admin FROM users WHERE email = '...';` (no re-login needed after the update) |

## Highest-risk areas in this part

1. **Webhook delivery is the only way a product learns about a subscription change.** It depends on one background worker per instance, the key-encryption secret, a public callback and
   at-least-once semantics with no ordering guarantee. Review `WebhookDeliveryService.DeliverNextAsync` (long transaction around an HTTP call, deadline logic) and alerting on `auth_webhook_outbox_messages{status="failed"}`.
2. **The platform admin flag is a single, unaudited database column that gates the whole admin area** (including signing-key rotation, which invalidates every token). Review who can run SQL on `users`, and add an audit trail
   or a documented procedure for granting and removing it.
3. **Client secrets and webhook secrets are shown once and cannot be recovered**, and secret rotation with `graceHours: 0` or `overlapHours: 0` breaks a product that has not switched. Review the runbook for rotations and
   the plan for a lost `Auth:Tokens:KeyEncryptionKey` (all webhook secrets and signing keys become unreadable).
4. **Subscription gating is enforced only at token exchange, and tokens last 15 minutes.** A suspension or revoked seat does not cut a live product token. Also the Owner auto-grant ignores the seat limit. Review
   `TokenExchangeService` and `SubscribeTenantService.GrantOwnersAccessAsync` against the product's expectations.
5. **Webhook address handling is a server-side request forgery surface.** Review `WebhookUrlValidator`, the connect-time check in `WebhookHttp.CreateHandler`, and make sure `Auth__Webhooks__AllowPrivateAddresses`
   is never set in production.
6. **The service-token endpoint is limited per IP only.** Behind a proxy that is not listed in `Auth:Proxy`, one limit is shared by every product, and a burst can lock all of them out. Review the proxy settings and product-side token caching.

## Unknowns and assumptions to confirm

- **Assumption (confirm):** the empty `403` body (no `code`) on admin and service routes is acceptable; no handler adds one.
- **Assumption (confirm):** the audience uniqueness is case-sensitive (key is lowercased in code, audience is not; the database collation is not set anywhere I read).
- **Assumption (confirm):** expired rows in `application_secrets` are meant to stay (nothing deletes them).
- **Assumption (confirm):** products implement their own subscribe call; no helper ships in `src/AuthService.Authorization` or `samples`.
- **Assumption (confirm):** a five-minute timestamp tolerance is suitable for webhook receivers (the service defines none).
- **Assumption (confirm):** a changed or wrong `Auth:Tokens:KeyEncryptionKey` shows up in `webhook_outbox.last_error` as an exception type name such as `AuthenticationTagMismatchException`.
- **Unknown:** whether any production product already verifies webhook signatures; ask each product owner.
- **Unknown:** whether the missing product edit/delete routes and the missing webhook outbox clean-up are intended; no ticket I read says so.
- **Inconsistency to resolve:** `docs/deployment.md` says an admin must "log in again for a token that carries it"; the code does not put the flag in the token.
