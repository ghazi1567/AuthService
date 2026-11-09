# Platform and cross-cutting behaviour

> Part 5 of the Auth Service reference. Overview, glossary and symptom index: [README](README.md).

This part covers the behaviour that every feature relies on and that does not belong to one feature: the audit log, email delivery,
rate limiting, the client address behind reverse proxies, paging, health and metrics, error responses, authorization policies, and the
database with its background workers. The full list of settings is in [07 Configuration reference](07-configuration-reference.md).

Conventions used below: file paths are relative to the repository root; `Auth:Proxy:ForwardLimit` is the configuration form and
`Auth__Proxy__ForwardLimit` is the environment-variable form of the same setting; "the API" is `src/AuthService.Api`.

## Contents

1. [Audit log](#audit-log)
2. [Email delivery](#email-delivery)
3. [Rate limiting](#rate-limiting)
4. [Client address and reverse proxies](#client-address-and-reverse-proxies)
5. [Paging](#paging)
6. [Health, readiness and metrics](#health-readiness-and-metrics)
7. [Error responses](#error-responses)
8. [Authorization policies and the endpoint inventory](#authorization-policies-and-the-endpoint-inventory)
9. [Database, migrations and background workers](#database-migrations-and-background-workers)
10. [Symptom to where to look (this part)](#symptom-to-where-to-look-this-part)
11. [Highest-risk areas in this part](#highest-risk-areas-in-this-part)
12. [Unknowns and assumptions to confirm](#unknowns-and-assumptions-to-confirm)

---

## Audit log

### What it does

The audit log is the permanent record of who did what, when, in which organisation (tenant) and from which address. A platform admin
reads the whole log. A tenant admin reads the entries that concern their own organisation. It is used to investigate incidents (a locked
account, a role change, a leaked key) and to answer "who changed this?".

Rules it enforces:

- **An entry exists if and only if the action happened.** `AuditLog.Record` only adds the entry to the `AuthDbContext`; the caller's own
  `SaveChangesAsync` commits it in the same transaction as the action. A signup that rolls back leaves no entry (test
  `AUD01_AnActionThatRollsBackLeavesNoAuditEntry`).
- **Details are short codes, never free text.** `detail` must match `^[a-z0-9_.-]{1,128}$` (lowercase letters, digits, dot, underscore,
  hyphen; at most 128 characters). Anything else makes `Record` throw `ArgumentException` ("An audit detail must be a short code ..."),
  so a password, token or email address cannot be recorded by mistake. The shape is deliberate: GUIDs, product keys and codes such as
  `wrong_password` fit; an email address (`@`) or a base64 token does not. Defined in `src/AuthService.Api/Features/Audit/AuditLog.cs`
  (`DetailPattern`). The column is `audit_entries.detail` `varchar(128)` (migration `AuditDetailUpTo128`).
- **Append-only, enforced by the database.** Triggers `audit_entries_append_only` (BEFORE UPDATE OR DELETE) and `audit_entries_no_truncate`
  (BEFORE TRUNCATE) call `audit_entries_guard()`, which raises `audit_entries is append-only: <op> is not allowed`. The only exception is
  a `DELETE` inside a transaction where `auth.audit_purge` is `on`, which only `AuditRetention.PurgeAsync` sets (with
  `set_config('auth.audit_purge', 'on', true)`, valid for that transaction only). Created by hand in migration `20261031194633_AddAuditLog`.
- **No endpoint edits or deletes entries.** There are no PUT/PATCH/DELETE routes (test `AUD_TC4_...`).
- **No foreign keys on purpose.** `audit_entries` refers to users, tenants and products by id only, so an entry outlives what it refers to
  (comment in `AuditEntryConfiguration`).
- **Retention: 365 days.** `Auth:Audit:RetentionDays` (default 365). A worker deletes older entries (see below).
- **Reading the log is itself audited**, after the query, so a read never lists itself: `auth.admin.audit_read` and `auth.tenant.audit_read`.

#### Who the actor can be

`AuditActorType` (`src/AuthService.Infrastructure/Persistence/Entities/AuditEntry.cs`), stored as a string in `actor_type`:

| Value | Meaning | Created by |
|---|---|---|
| `Anonymous` | Nobody could be identified, for example a login for an unregistered email | `AuditActor.Anonymous` |
| `User` | A person (global identity) | `AuditActor.User(userId)` |
| `Product` | A product acting with its service token | `AuditActor.Product(applicationId)` |
| `ApiKey` | Declared in the enum, but **no `AuditActor` factory creates it** (see Decisions and open questions) | none |
| `System` | The service itself, for example a scheduled key rotation | `AuditActor.System` |

#### The `AuditActions` catalogue

All codes are constants in `src/AuthService.Api/Features/Audit/AuditActions.cs` (39 in total). `action` is `varchar(64)`. The detail
column holds the value shown, or nothing where the cell says "none".

| Area | Constant | Value | Actor | Tenant set | Detail |
|---|---|---|---|---|---|
| Accounts | `SignupCompleted` | `auth.signup.completed` | User | yes (new tenant) | none |
| Accounts | `TenantCreated` | `auth.tenant.created` | User | yes | none |
| Accounts | `LoginSucceeded` | `auth.login.succeeded` | User | no | none |
| Accounts | `LoginFailed` | `auth.login.failed` | User, or Anonymous for an unknown email | no | `wrong_password`, `unknown_account`, `account_locked`, `account_disabled`, `email_not_verified` |
| Accounts | `AccountLocked` | `auth.account.locked` | User | no | `too_many_failed_logins` |
| Accounts | `EmailVerified` | `auth.email.verified` | User | no | none |
| Accounts | `PasswordResetCompleted` | `auth.password_reset.completed` | User | no | none |
| Accounts | `RefreshTokenReuseDetected` | `auth.refresh_token.reuse_detected` | User | no | none |
| Audit reads | `AdminAuditRead` | `auth.admin.audit_read` | User (platform admin) | no | none |
| Audit reads | `TenantAuditRead` | `auth.tenant.audit_read` | User | yes | none |
| Products (platform admin) | `ApplicationRegistered` | `auth.application.registered` | User | no | product key |
| Products | `ApplicationSecretRotated` | `auth.application.secret_rotated` | User | no | product key |
| Products | `PermissionsRegistered` | `auth.application.permissions_registered` | User | no | product key |
| Products | `ApplicationActivated` / `ApplicationDeactivated` | `auth.application.activated` / `auth.application.deactivated` | User | no | product key |
| Products | `ApplicationLaunchUrlSet` | `auth.application.launch_url_set` | User | no | product key |
| Products | `ApplicationRenamed` | `auth.application.renamed` | User | no | product key |
| Products | `RoleTemplatesSet` | `auth.application.role_templates_set` | User | no | product key |
| Products | `RoleTemplatesApplied` | `auth.application.role_templates_applied` | Product or User (whoever subscribed the organisation, or the platform admin who copied the roles) | yes | product key |
| Webhooks | `WebhookConfigured` | `auth.application.webhook_configured` | Product | no | product key |
| Webhooks | `WebhookSecretRotated` | `auth.webhook.secret_rotated` | Product | no | `overlap_hours_<n>` |
| Webhooks | `WebhookResent` | `auth.webhook.resent` | User (platform admin) | no | webhook id |
| Subscriptions | `TenantSubscribed` | `auth.subscription.subscribed` | Product (a User when a platform admin subscribes the tenant, ONB-02) | yes | product key |
| Subscriptions | `SubscriptionSuspended` / `SubscriptionCancelled` | `auth.subscription.suspended` / `auth.subscription.cancelled` | Product | yes | product key |
| Subscriptions | `SeatLimitChanged` | `auth.subscription.seat_limit_changed` | Product | yes | product key |
| Roles | `RoleCreated` / `RoleUpdated` / `RoleDeleted` | `auth.role.created` / `.updated` / `.deleted` | User | yes | role id |
| Members | `MemberRolesChanged` | `auth.membership.roles_changed` | User | yes | membership id |
| Members | `MemberDisabled` / `MemberEnabled` | `auth.membership.disabled` / `auth.membership.enabled` | User | yes | membership id |
| Members | `MemberAccessGranted` / `MemberAccessRevoked` | `auth.membership.access_granted` / `auth.membership.access_revoked` | User | yes | `<membership-id>.<product-key>` |
| Invitations | `InvitationSent` / `InvitationResent` / `InvitationRevoked` | `auth.invitation.sent` / `auth.invitation.resent` / `auth.invitation.revoked` | User | yes | membership id |
| Invitations | `InvitationAccepted` | `auth.invitation.accepted` | User (the invitee) | yes | membership id |
| API keys | `ApiKeyCreated` / `ApiKeyRotated` / `ApiKeyRevoked` | `auth.apikey.created` / `.rotated` / `.revoked` | User | yes | key id (for rotate: the replacement key's id) |
| Signing keys | `SigningKeyCreated` | `auth.signing_key.created` | System | no | `initial` |
| Signing keys | `SigningKeyRotated` | `auth.signing_key.rotated` | System (scheduled) or the admin (emergency) | no | `scheduled` or `emergency` |

Not audited: token exchange, service-token requests, logout, password-reset requests (only the completion), email-verification resends,
and failed attempts at the admin or tenant endpoints. Search the code for `audit.Record` before relying on an event being present.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `GET /api/auth/admin/audit` | Platform admin (`PlatformAdmin` policy on the `/api/auth/admin` group) | `AuditAdminEndpoints.GetAuditAsync` | `AuditPaging.PageAsync` | Whole log, newest first. Filters `action`, `tenantId`, `actorId`, `before`, `beforeId`, `limit` |
| `GET /api/auth/tenants/me/audit` | Tenant token holding `audit.read` at `ReadOnly` or above | `TenantAuditEndpoints.GetAsync` | `AuditPaging.PageAsync(..., tenantScoped: true)` | Only the token's tenant. Filters `action`, `actorId`, `before`, `beforeId`, `limit`. A `tenantId` query value is never read |

Flow of a recorded event:

1. A service builds its change (for example `LoginService.LoginAsync` adds a refresh token).
2. It calls `audit.Record(AuditActions.X, AuditActor.User(id), tenantId, detail)`. `Record` stamps `Id = Guid.CreateVersion7()`,
   `OccurredAt = timeProvider.GetUtcNow()` and `SourceIp = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString()`.
3. The service's `SaveChangesAsync` writes the change and the entry together. Failed logins and lockouts save at once
   (`LoginService.RecordFailureAsync`, `RegisterFailureAsync`) because nothing else is waiting on them.

Flow of a read (`AuditPaging.PageAsync`):

1. Validate `limit` (1 to 200, default 50, `AuditPaging.DefaultLimit` and `MaxLimit`); otherwise `400` validation problem with an error under `limit`.
2. Filter by tenant (if given), action, actor; apply the cursor `(occurred_at, id) < (before, beforeId)`.
3. Order `occurred_at DESC, id DESC`, read `limit + 1` rows to know whether there is a next page.
4. `ActorNamesAsync` resolves names in two small reads (users, then products) and `actorName` is filled in.
5. The caller records its own read (`AdminAuditRead` or `TenantAuditRead`) and saves.

Response shape: `{ "items": [ { id, occurredAt, action, actorType, actorId, tenantId, sourceIp, detail, actorName } ], "next": { before, beforeId } | null }`.

#### `actorName` resolution (AUD-02)

- A **person** is named by `users.display_name`, or by `users.email` when there is no display name.
- A **product** is named by `applications.name`.
- `Anonymous` and `System` actors, and any entry with no `actor_id`, have `actorName: null`.
- **Who may see names.** A platform admin sees every name. A tenant admin sees a person's name **only if that person has a membership in
  the admin's tenant** (`tenant_memberships` row with the same `tenant_id`, any status). A person from another organisation, or a platform
  admin acting from outside the tenant, stays `null`, so their email is not disclosed. Product names are shown to both readers.

Data:

| Table / entity | Columns |
|---|---|
| `audit_entries` / `AuditEntry` | `id` (uuid v7, primary key), `occurred_at`, `action` (64), `actor_type` (20, string), `actor_id` (null), `tenant_id` (null), `source_ip` (45), `detail` (128). Indexes: `occurred_at`; `(tenant_id, occurred_at)`; `(actor_id, occurred_at)` |

Background worker: `AuditRetentionWorker` (below).

Libraries: EF Core with Npgsql; PostgreSQL triggers (PL/pgSQL).

#### Retention worker

`AuditRetentionWorker` (`src/AuthService.Api/Features/Audit/AuditRetention.cs`) runs `AuditRetention.PurgeAsync` once at startup and then every
`Auth:Audit:RetentionCheckSeconds` (default 86 400). `PurgeAsync` computes `cutoff = now - RetentionDays`, opens a transaction, sets
`auth.audit_purge` on for that transaction and runs `ExecuteDeleteAsync` for `occurred_at < cutoff`.

- If `Auth:Audit:RetentionEnabled` is `false` the worker logs `The audit retention worker is disabled; old entries will not be deleted automatically.` and exits.
- On an error it logs `The audit retention job failed; trying again later.` and retries after the interval, or after one hour if the interval is longer.
- When it deletes something it logs `Audit retention removed {Removed} entries older than {RetentionDays} days.`
- If it never runs: nothing breaks, the table only grows.

### Configuration and environment

| Setting (both forms) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Audit:RetentionDays` / `Auth__Audit__RetentionDays` | `365` | `AuditOptions` | Entries older than this are deleted at the next run. Lowering it deletes history at the next run, and there is no undo |
| `Auth:Audit:RetentionEnabled` / `Auth__Audit__RetentionEnabled` | `true` | `AuditOptions` | `false` stops all automatic deletion (the integration test factory sets it to `false`) |
| `Auth:Audit:RetentionCheckSeconds` / `Auth__Audit__RetentionCheckSeconds` | `86400` | `AuditOptions` | How often the job runs; minimum effective value is 1 second |
| `Auth:Proxy:*` / `Auth__Proxy__*` | none | `ProxyOptions` | Decides whether `source_ip` is the visitor or the proxy (next sections) |

Nothing in the audit feature is a secret.

### Failure modes and production debugging

What the caller sees:

| Situation | Status and code |
|---|---|
| `limit` outside 1 to 200 | `400`, validation problem with an error under `limit` ("Limit must be between 1 and 200.") |
| Missing or invalid token | `401` `unauthorized` |
| Tenant token without `audit.read` (or an identity token, or a product token for another product) | `403` with the framework's default empty body (see Error responses) |
| Signed-in non-admin calls `/api/auth/admin/audit` | `403`, default empty body |

What is logged: only the retention messages above. A rejected `detail` surfaces as an `ArgumentException` inside the calling request, which
becomes a `500` (unhandled-exception behaviour) and the action is not saved.

Known edge cases:

- **Platform admin read records its own entry only if the token has a user id** (`caller.GetUserId() is { } adminId`).
- **Tenant admin never sees entries with no tenant.** Login events, signup of the first tenant (tenant is set, so visible), and admin-only
  events (`tenant_id` null) are not visible to a tenant admin. This is by design (`AUD01_EntriesThatBelongToNoTenantAreNotShownToATenantAdmin`).
- **`before` without `beforeId` is accepted** by the audit endpoints (it filters on time only). `PageQuery` lists reject that with `400`. See Paging.
- **A deleted-then-recreated reference still names someone.** `actorName` is resolved at read time from the current `users` and
  `applications` rows. If a display name changes, old entries show the new name.

Likely production causes:

| Symptom | Cause | How to confirm |
|---|---|---|
| Entries show the proxy's address (for example a Docker gateway) | `Auth:Proxy` not set, or the proxy's address is outside the listed range | `SELECT source_ip, count(*) FROM audit_entries WHERE occurred_at > now() - interval '1 hour' GROUP BY 1 ORDER BY 2 DESC;` If one private address dominates, it is the proxy |
| `source_ip` is null | The entry was made outside a request (system actions such as the first signing key at startup) | Entries with `actor_type = 'System'` |
| Expected event missing | The action is not in the "Not audited" list above, or the action failed and rolled back | `SELECT action, count(*) FROM audit_entries GROUP BY 1 ORDER BY 1;` and search the code for the constant |
| Table grows without end | Retention worker disabled, or failing | Look for `The audit retention worker is disabled` or `The audit retention job failed` in the logs; `SELECT min(occurred_at) FROM audit_entries;` |
| A manual cleanup fails with `audit_entries is append-only: DELETE is not allowed` | The trigger works as designed | Do not drop it; set `auth.audit_purge` inside your own transaction only for a deliberate purge |
| `actorName` is null for a person you expect | The reader is a tenant admin and the person has no membership in their tenant, or the user row was removed | `SELECT 1 FROM tenant_memberships WHERE tenant_id = '<tenant>' AND user_id = '<actor>';` |

Read-only SQL to trace one person's recent activity:

```sql
SELECT occurred_at, action, tenant_id, source_ip, detail
FROM audit_entries
WHERE actor_id = '<user id>'
ORDER BY occurred_at DESC, id DESC
LIMIT 50;
```

### Decisions and open questions

- **Why short codes.** The detail rule makes it impossible to record a password, token or address; the trade-off is that it cannot hold a
  human sentence. The README states the same rule.
- **Why the database enforces append-only.** Application code could be changed to edit entries; the triggers are a second lock. The README
  notes that the application's own database user can still drop the triggers; for stronger protection give it only `INSERT` and `SELECT`
  on the table and run retention as a separate role. This is not done in the shipped compose file (the API uses the one database user).
- **Open question: `ApiKey` actor type is unused.** `AuditActorType.ApiKey` exists and the README says the audit log has an "API key" actor,
  but `AuditActor` has no factory for it and no code records it. API-key admin events are recorded with the creating user as actor.
  **Unknown:** whether API-key calls (`POST /api/auth/apikeys/validate`) are meant to be audited later.
- **Inconsistency: `AuditEntry` comment vs code.** The comment on `AuditEntry.Detail` says "Lowercase letters, digits, dots and underscores
  only", but the regex also allows hyphens (needed for GUIDs and `membership-id.product-key`). The code is right; the comment is stale.
- **Inconsistency: README "Audit log" sentence** "API-key events are recorded when those stories land" is out of date: `ApiKeyCreated`,
  `ApiKeyRotated` and `ApiKeyRevoked` exist and are recorded.
- **Assumption (confirm):** `source_ip` for an IPv6 or IPv4-mapped-IPv6 peer is stored exactly as `IPAddress.ToString()` gives it (no mapping to
  IPv4). I did not see a mapping in `AuditLog.Record`; the 45-character column is wide enough for the longest IPv6 text.

---

## Email delivery

### What it does

The service sends three kinds of email: an **email verification** link (after signup and on resend), a **password reset** link, and an
**invitation** to join an organisation. People use them to get into the product, so they must arrive. Sending never happens inside the
web request: the request only writes a row to a queue (the "outbox"), and a background worker sends it. A slow or failing email provider
therefore never fails a signup or an invitation.

Rules it enforces:

- **Queued in the same transaction as the change that caused it.** `EmailOutbox.Enqueue` only adds a row to the context; the caller's
  `SaveChangesAsync` commits it with the change (signup, invitation, reset request). If the change rolls back, no email exists.
- **The template is rendered once at queue time to catch mistakes early** (`EmailOutbox.Enqueue` calls `EmailTemplates.Render` and discards
  the result). A missing `token` or `tenantName` throws `InvalidOperationException("The email template needs a '<key>' value.")` in the
  request that made the mistake, and nothing is queued.
- **Retries with growing delays.** `Email:MaxAttempts` (default 5). Delay after failed attempt n is `RetryDelaySeconds[min(n-1, last)]`; the
  default is `[30, 120, 600, 3600]` seconds (`EmailOptions.DefaultRetryDelaySeconds`), so attempts happen at roughly 0, 30 s, 2.5 min,
  12.5 min and 72.5 min. After the last failed attempt the row becomes `Failed` and is never tried again.
- **The token is removed once the email is sent or given up on.** `email_outbox.payload_json` (which holds the token) is set to null on
  `Sent` and on `Failed`. Consequence: a `Failed` row cannot be re-queued by setting `status = 'Pending'`; the payload is gone and the next
  attempt would fail with the missing-token error. The person must ask again (new verification or reset request, or the admin resends the invitation).
- **Logs never contain addresses, tokens or links** (test `NTF_TC4_LogsNeverContainTokenValuesLinksOrAddresses`).
- **Delivery is exactly-once per message across instances.** `EmailDeliveryService.DeliverNextAsync` selects one due row per transaction with
  `FOR UPDATE SKIP LOCKED`, so several instances take different messages (test `NTF01_Delivery_WorkersRunningAtTheSameTimeSendEachEmailExactlyOnce`).
  A crash after the provider accepted but before the commit can send twice (at-least-once in that window).

#### The templates

`src/AuthService.Api/Email/EmailTemplates.cs`. Each has a fixed HTML and text body. The link is
`{Email:PublicBaseUrl without trailing slash}/{path}?token={url-encoded token}`.

| `EmailTemplate` | Subject | Link path | Text in the email about expiry (default; the queued value replaces the time) | Queued by |
|---|---|---|---|---|
| `Verification` | `Verify your email address` | `/verify-email` | "This link expires in 24 hours." | `EmailVerificationService` (signup and resend) |
| `PasswordReset` | `Reset your password` | `/reset-password` | "This link expires in 1 hour and can be used once." | `PasswordResetService` |
| `Invitation` | `You have been invited to <tenantName>` | `/accept-invitation` | "This invitation expires in 7 days." | `InviteUserService`, `ManageInvitationsService` (resend) |

The tenant name is HTML-encoded in the HTML body; the token is URL-encoded in the link.

### How it is implemented

There are no HTTP entry points for email. The relevant code:

| Part | File | Role |
|---|---|---|
| Queue | `src/AuthService.Api/Email/EmailOutbox.cs` | `Enqueue(template, toAddress, data)` adds an `EmailOutboxMessage` with `Status = Pending`, `NextAttemptAt = now` |
| Worker | `src/AuthService.Api/Email/EmailDispatcher.cs` | `BackgroundService`; every `Email:DispatchIntervalMilliseconds` calls `EmailDeliveryService.DeliverDueAsync` |
| Delivery | `src/AuthService.Api/Email/EmailDeliveryService.cs` | Takes up to 50 due messages per pass (`BatchLimit`), one transaction each |
| Provider choice | `src/AuthService.Api/Email/EmailSenderFactory.cs` | Reads `Email:Provider`; resolved at startup (`app.Services.GetRequiredService<IEmailSender>()` in `Program.cs`) |
| Providers | `src/AuthService.Api/Email/Providers/` | `BrevoEmailSender`, `FileEmailSender`, `InMemoryEmailSender` |
| Templates | `src/AuthService.Api/Email/EmailTemplates.cs` | `Render` |

Flow:

1. A feature service (for example `EmailVerificationService`) creates its token row and calls `outbox.Enqueue(EmailTemplate.Verification, email, { token })`.
2. Its `SaveChangesAsync` commits the token row and the `email_outbox` row (status `Pending`) together.
3. `EmailDispatcher` wakes up (default every 2 000 ms; minimum 10 ms) and creates a scope.
4. `EmailDeliveryService.DeliverNextAsync` opens a transaction and runs `SELECT * FROM email_outbox WHERE status = 'Pending' AND next_attempt_at <= now ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED`.
5. It renders the template and calls `IEmailSender.SendAsync`.
6. Success: `attempts++`, `status = Sent`, `sent_at` set, `last_error = null`, `payload_json = null`; logs `Email {OutboxId} ({Template}) sent after {Attempts} attempt(s).`
7. Failure: `attempts++`, `last_error` = the `EmailSendException` message or just the exception type name. If `attempts >= MaxAttempts`: `status = Failed`, payload cleared, log `Email {OutboxId} ({Template}) failed for good after {Attempts} attempt(s): {Error}`. Otherwise `next_attempt_at = now + delay`, log `Email {OutboxId} ({Template}) attempt {Attempts} failed, will retry in {DelaySeconds}s: {Error}` (warning).
8. The transaction commits; the loop continues until no due message is left or 50 were handled.

#### Providers

| Provider (`Email:Provider`, case-insensitive) | What it does | Needs |
|---|---|---|
| `Brevo` | `POST https://api.brevo.com/v3/smtp/email` with header `api-key`; HTTP client named `brevo`, timeout 15 s | `Email:BrevoApiKey`, and `Email:FromAddress` set to something other than the default `no-reply@localhost` |
| `File` | Writes `To:`, `Subject:` and the text body to a `.txt` file named `yyyyMMddHHmmssfff-<guid>.txt` | Directory `Email:FileDirectory`, default `<content root>/artifacts/emails` |
| `InMemory` | Keeps messages in a `ConcurrentQueue`; used by tests | nothing |

If `Email:Provider` is blank: Development uses `File`, environment `Testing` uses `InMemory`, any other environment **throws at startup**
(`Email:Provider is not configured.`).

Brevo errors stored in `email_outbox.last_error` (never the response body): `Brevo could not be reached.`, `Brevo did not answer in time.`,
`Brevo returned <status>.`

Data (`email_outbox` / `EmailOutboxMessage`): `id`, `to_address` (254), `template` (`Verification`, `PasswordReset`, `Invitation`; string),
`payload_json`, `status` (`Pending`, `Sent`, `Failed`; string), `attempts`, `next_attempt_at`, `last_error` (500), `created_at`, `sent_at`.
Index `(status, next_attempt_at)`. State machine: `Pending` -> `Sent`, or `Pending` -> (retries, still `Pending`) -> `Failed`. Nothing moves a row out of `Sent` or `Failed`.

Libraries and services: Brevo transactional email API; `IHttpClientFactory`; EF Core raw SQL for the lock.

Background worker: `EmailDispatcher` (setting `Email:DispatchIntervalMilliseconds`; if it is not running, emails stay `Pending` and nobody can
verify, reset or accept; `auth_email_outbox_messages{status="pending"}` and `auth_email_outbox_oldest_pending_age_seconds` grow).

### Configuration and environment

| Setting (both forms) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Email:Provider` / `Email__Provider` | none (Development `File`, Testing `InMemory`) | `EmailOptions` | Required elsewhere; unsupported value stops startup: `Email:Provider 'X' is not supported (use Brevo, File or InMemory).` |
| `Email:BrevoApiKey` / `Email__BrevoApiKey` | none | `EmailOptions` | **Secret.** Required with Brevo; missing stops startup: `Email:BrevoApiKey is not configured.` |
| `Email:FromAddress` / `Email__FromAddress` | `no-reply@localhost` | `EmailOptions.DefaultFromAddress` | With Brevo it must be a sender verified in Brevo; blank or the default stops startup: `Email:FromAddress must be set to a verified sender for Brevo.` |
| `Email:FromName` / `Email__FromName` | `Auth Service` | `EmailOptions` | Display name of the sender |
| `Email:PublicBaseUrl` / `Email__PublicBaseUrl` | `http://localhost:5080` | `EmailOptions` | Where links point. A wrong value makes every link dead. Compose sets it to `https://<APP_HOST>` |
| `Email:FileDirectory` / `Email__FileDirectory` | `artifacts/emails` under the content root | `EmailOptions` | Only the `File` provider. Compose sets `/tmp/emails` |
| `Email:DispatcherEnabled` / `Email__DispatcherEnabled` | `true` | `EmailOptions` | `false`: worker logs `The email dispatcher is disabled; queued emails will wait.` and exits |
| `Email:DispatchIntervalMilliseconds` / `Email__DispatchIntervalMilliseconds` | `2000` | `EmailOptions` | Polling delay; values under 10 are raised to 10 |
| `Email:MaxAttempts` / `Email__MaxAttempts` | `5` | `EmailOptions` | Tries before `Failed` |
| `Email:RetryDelaySeconds` / `Email__RetryDelaySeconds__0` ... | `[30,120,600,3600]` (in code) | `EmailOptions` | The array in configuration **appends** to existing values, so the default lives in code and the array is null until set; an empty array means no delay |
| `Auth:EmailVerification:TokenHours`, `ResendCooldownSeconds` | `24`, `60` | `EmailVerificationOptions` | Link lifetime; minimum gap before a new link is queued for the same account |
| `Auth:PasswordReset:TokenMinutes`, `RequestCooldownSeconds` | `60`, `60` | `PasswordResetOptions` | Same, for reset |
| `Auth:Invitations:TokenDays`, `ResendCooldownSeconds` | `7`, `60` | `InvitationOptions` | Same, for invitations |

### Failure modes and production debugging

What the caller sees: nothing, in the normal case. A signup, reset request or invitation succeeds whatever the provider does. The two
exceptions are a template mistake at queue time (`500`) and a missing migration (`500`).

What is logged: the four messages in the flow above, plus `Delivering queued email failed; trying again shortly.` when the database is
briefly unreachable (the emails stay queued). Metrics: `auth_email_outbox_messages{status="pending"|"failed"}` and
`auth_email_outbox_oldest_pending_age_seconds`.

Likely causes when "emails are not arriving", in the order to check:

| Cause | How to confirm |
|---|---|
| Worker not running or disabled | Log `The email dispatcher is disabled`; `auth_email_outbox_oldest_pending_age_seconds` rising; `SELECT status, count(*), min(created_at) FROM email_outbox GROUP BY status;` shows old `Pending` |
| Brevo refusing (unverified sender, bad key) | `SELECT id, template, attempts, last_error, next_attempt_at FROM email_outbox WHERE status <> 'Sent' ORDER BY created_at DESC LIMIT 20;` `last_error` is `Brevo returned 401.` (key) or `Brevo returned 400.` (sender or payload) |
| Brevo unreachable | `last_error` is `Brevo could not be reached.` or `Brevo did not answer in time.`; egress/DNS from the API container |
| `Failed` rows | `Failed` is final. The user must ask again. Metric `auth_email_outbox_messages{status="failed"}` above 0 |
| Accepted by Brevo but never delivered (bounce, spam) | `email_outbox` shows `Sent`. The service does not hear about bounces: check Brevo's Transactional Logs (`docs/deployment.md`, "Production email") |
| Link opens a wrong or dead page | `Email:PublicBaseUrl` wrong; the default points at port 5080, which is the API in `launchSettings.json`, not the web console (port 5090) |
| Resend cooldown | Verification, reset and invitation resends within the cooldown (60 s) queue nothing; invitations answer `429` `resend_too_soon` |
| Startup fails | Log shows one of the `Email:...` exceptions above; the API container restarts in a loop; `/health/ready` never answers |
| Emails go to the dev folder | Provider is `File` (Development default, or `EMAIL_PROVIDER=File` in compose); files are in `Email:FileDirectory` |

### Decisions and open questions

- **Outbox rather than inline sending** (NTF-01): provider failures never fail user requests, and the email exists only if the change committed.
- **Token cleared on completion**, so a database dump or backup after the fact does not hold live links. Cost: no manual re-queue.
- **Expiry text follows the settings (AUTH-266).** Each feature queues the lifetime it used (`EmailLifetime.Describe`, data key `validFor`, for
  example "30 minutes", "1 hour", "2 days"), and `EmailTemplates.Render` writes it into the footer. An email queued without it (before this
  change) falls back to the old default sentence. Tests in `EmailLifetimeTests`.
- **`Email:PublicBaseUrl` default** (`http://localhost:5080`) is the API's development port, while the web console that handles the link
  paths is on `http://localhost:5090`. `EmailSenderFactory` makes the Brevo provider refuse to start unless the address is absolute `https`;
  File and InMemory still accept the default.
- **Risk: the provider call happens inside an open database transaction** holding the row lock (up to the 15 s Brevo timeout). With
  `SKIP LOCKED` other instances are not blocked, but a very slow provider keeps connections busy.
- **Risk: no automatic requeue or alert for `Failed`.** Only the metric exists.
- **Assumption (confirm):** the 50-message `BatchLimit` per pass with a 2 s pause is enough for expected volume (up to roughly 25 emails a second
  per instance when the provider answers quickly); I did not see a load figure in the specification.

---

## Rate limiting

### What it does

Rate limiting slows down guessing and email flooding on the few endpoints that can be abused without a login. Each limited endpoint has a
**policy**: a counter per caller, a limit, and a window. When a caller goes over the limit within the window, the endpoint answers `429`.
The counters are kept in the service's own PostgreSQL database, so every instance of the API shares them (decision
`docs/decisions/0008-rate-limit-counters-in-postgresql.md`).

#### The five policies

Resolved in `RateLimitPolicies.Resolve` (`src/AuthService.Api/RateLimiting/SharedRateLimit.cs`). Settings are read per request.

| Policy name | Endpoints | Counted per | Limit and window (defaults) | Settings |
|---|---|---|---|---|
| `login` | `POST /api/auth/login` | client IP, across all accounts | 20 per 1 minute | `Auth:LoginProtection:IpLimit`, `IpWindowMinutes` |
| `password-reset` | `POST /api/auth/password/forgot` and `POST /api/auth/password/reset` (one shared counter) | client IP | 10 per 10 minutes | `Auth:PasswordReset:IpLimit`, `IpWindowMinutes` |
| `service-token` | `POST /api/auth/token` | client IP, across all products | 60 per 1 minute | `Auth:Applications:TokenIpLimit`, `TokenIpWindowMinutes` |
| `token-exchange` | `POST /api/auth/token/exchange` | signed-in person (`user:<id>`); `ip:<address>` if no valid user | 30 per 1 minute | `Auth:TokenExchange:UserLimit`, `UserWindowMinutes` |
| `email-verification-resend` | `POST /api/auth/email/verify/resend` | client IP | 10 per 10 minutes | `Auth:EmailVerification:ResendIpLimit`, `ResendIpWindowMinutes` |

Other protections are **not** this limiter: the per-account lockout in `LoginService` (5 wrong passwords, 15 minutes), the per-account
cooldowns for emails (60 s), and the invitation resend `429` `resend_too_soon`.

Rules:

- **Fixed windows.** A window starts at the first use and ends one window later; then the count starts again.
- **Refused attempts count too.** `rate_limit_counters.hits` includes the ones refused.
- **Every request to a limited endpoint is counted**, successful or not (the middleware runs before the handler).
- **Fail closed.** If the counter store throws, the request is refused with `503` (below). No limit fails open.
- **Keys are hashed.** The key `"<policy>|<address or user:id>"` is hashed with SHA-256 and stored as base64url (43 characters); no address
  or person id is kept.
- **Unknown address.** If `Connection.RemoteIpAddress` is null the partition is the string `unknown`, one shared bucket.

### How it is implemented

| Part | File | Role |
|---|---|---|
| Marking an endpoint | `RequireSharedRateLimit(policy)` (`SharedRateLimit.cs`) | Adds `SharedRateLimitMetadata(policy)` to the endpoint |
| Middleware | `SharedRateLimitMiddleware` | Runs after `UseAuthentication` and before `UseAuthorization` (`Program.cs`) so a limit can be per person |
| Rules | `RateLimitPolicies.Resolve` | Builds `RateLimitRule(Key, Limit, Window)` |
| Store (default) | `PostgresRateLimitStore` (`RateLimitStore.cs`) | One atomic `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` per use; time is the database's `clock_timestamp()` |
| Store (alternative) | `InMemoryRateLimitStore` | `ConcurrentDictionary` with the process's steady clock |
| Cleanup | `RateLimitCleanupWorker` | Deletes counters whose windows ended more than 1 minute ago |
| Settings | `RateLimitOptions` (`Auth:RateLimit`) | `Store`, `CleanupSeconds`, validation |

Request flow:

1. `RequestMetricsMiddleware` and the exception handler run, then `UseAuthentication` (the JWT is validated locally, no database call).
2. `SharedRateLimitMiddleware` reads the endpoint metadata; no metadata means the request passes untouched.
3. `RateLimitPolicies.Resolve` builds the key and limit from the options.
4. `store.HitAsync(key, window)` counts the use and returns `(Hits, WindowRemaining)`.
5. Store throws (and the request was not aborted): logs the error, sets `Retry-After: 5` and answers `503` code `rate_limit_unavailable`, title `Temporarily unavailable`.
6. `Hits > Limit`: sets `Retry-After` to the seconds left in the window (at least 1) and answers `429` code `rate_limited`, title `Too many requests`, detail `Try again later.`
7. Otherwise the pipeline continues.

Data (`rate_limit_counters` / `RateLimitCounter`): `key_hash` (primary key, 64), `hits` (bigint), `expires_at`. Index on `expires_at`. The upsert
restarts the count to 1 in the same statement when the existing window has ended. The cleanup SQL is
`DELETE FROM rate_limit_counters WHERE expires_at < clock_timestamp() - interval '1 minute'`.

Memory mode: counters live in each process, a restart clears them, and with N instances a limit is effectively N times the setting. It does
not follow a test clock (uses `Stopwatch`). `appsettings.Development.json` sets `Auth:RateLimit:Store` to `Memory`; the integration-test factory does too.

Background worker: `RateLimitCleanupWorker` waits `Auth:RateLimit:CleanupSeconds` (default 60), then purges; it runs in both modes. If it
stops, expired rows remain but are harmless: a window that is over is replaced on the next use. The table just grows.

### Configuration and environment

| Setting (both forms) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:RateLimit:Store` / `Auth__RateLimit__Store` | `Postgres` | `RateLimitOptions` | `Memory` makes limits per process. Anything else (including an empty value) stops startup with `Auth:RateLimit:Store is '<x>', which must be 'Postgres' or 'Memory'.` |
| `Auth:RateLimit:CleanupSeconds` / `Auth__RateLimit__CleanupSeconds` | `60` | `RateLimitOptions` | Less than 1 stops startup: `Auth:RateLimit:CleanupSeconds must be at least 1.` |
| The per-policy limits and windows | see the table above | the feature options classes | A limit of 0 or less refuses every request (not validated). A longer window means a longer lock-out |
| `Auth:Proxy:*` | none | `ProxyOptions` | Needed for the limits to see the client rather than the proxy |

### Failure modes and production debugging

| What the caller sees | Cause |
|---|---|
| `429` `rate_limited` with `Retry-After: <seconds>` | The caller's address (or person) exceeded the limit in the window |
| `503` `rate_limit_unavailable` with `Retry-After: 5` | The counters could not be read or written: database unreachable, **or the `AddRateLimitCounters` migration (`rate_limit_counters` table) has not been applied** |

What is logged: `The rate-limit store could not be reached, so a request to a limited endpoint was refused ({Policy}).` (error, with the
exception; the policy name is logged, never the caller). Cleanup failures log `Removing expired rate-limit counters failed; trying again shortly.` (warning).
Metrics: `auth_http_requests_total{route="/api/auth/login",status="429"}` or `"503"`.

Likely causes:

| Symptom | Cause | Confirm |
|---|---|---|
| Everyone gets `429` on login | The proxy is not in `Auth:Proxy`, so every visitor shares one address; or the web console is not forwarding the visitor's address | `SELECT hits, expires_at FROM rate_limit_counters ORDER BY hits DESC LIMIT 5;` (one row with a high count); `SELECT source_ip, count(*) FROM audit_entries GROUP BY 1 ORDER BY 2 DESC LIMIT 5;` shows one address |
| Limited endpoints return `503` right after a deploy | Migration not run | `/health/ready` also `503` with `database: failed`; log `Not ready: the database is missing {Count} migrations.` |
| Limited endpoints return `503` but the rest works | Connection pool exhausted or a database permission/lock problem on `rate_limit_counters` | The error log above includes the exception |
| Limit seems higher than configured | `Store=Memory` with several instances, or `IpLimit` set high | Check `Auth__RateLimit__Store` in the container's environment |
| Service will not start after setting the store | Typo or blank value | Startup exception mentions `Auth:RateLimit` |
| A single visitor is blocked after a few attempts | Defaults are small by design: 20 logins per minute, 10 reset or resend requests per 10 minutes | Wait for `Retry-After`, or `DELETE FROM rate_limit_counters` (deliberate operator action; the table is only counters) |

### Decisions and open questions

- **Counters in PostgreSQL, not Redis** (decision 0008): no extra infrastructure; endpoints are not hot paths (one small write per request).
  `IRateLimitStore` is the seam if a deployment outgrows this.
- **Fail closed** (decision 0008): a store outage must not turn into unlimited guessing. Cost: a database fault also stops these endpoints,
  which could not have worked anyway.
- **Token exchange is per person, not per address**, so one looping client cannot starve everyone and shared addresses do not penalise people.
- **Risk: `password-reset` is one shared counter for `forgot` and `reset`.** A user who requests several links can use up the budget needed to complete a reset.
- **Risk: limits are not validated.** Only `Store` and `CleanupSeconds` are checked at startup; a zero or negative limit locks the endpoint.
- **Assumption (confirm):** the rate-limit and audit code use `RemoteIpAddress.ToString()` directly. On a dual-stack listener an IPv4 client can
  appear as `::ffff:a.b.c.d`; I did not see code that maps it for these two uses (the metrics filter and the web console's handler do).

---

## Client address and reverse proxies

### What it does

Behind a reverse proxy (Caddy in the shipped compose stack, or a load balancer) every connection to the API comes from the proxy. Without
help, all per-IP rate limits would share one budget and every audit entry would name the proxy. `Auth:Proxy` lists the proxies whose
`X-Forwarded-For` header is believed, so the client's address replaces the proxy's in both places.

Rules:

- **Only listed proxies are believed.** A connection from any other address has its `X-Forwarded-For` ignored, so a caller cannot choose their own address.
- **With nothing listed the header is never read** and the middleware is not added at all.
- **Loopback is not trusted by default.** `ProxyOptions.ToForwardedHeadersOptions` clears the framework's default `KnownProxies` and `KnownIPNetworks` (which trust loopback) and adds only what is listed.
- **Only the client address is taken** (`ForwardedHeaders.XForwardedFor`), not scheme or host. `Auth:Proxy:ForwardLimit` (1 to 10, default 1) is the number of entries, counted from the right, that are believed: the number of proxies in front of the service.
- **The middleware runs first** (`app.UseForwardedHeaders` before metrics, exception handling, authentication and the limiter), so everything after sees the real client.
- **Blank entries are ignored**, because hosting platforms pass empty variables for unused settings.

### How it is implemented

| Part | File | Role |
|---|---|---|
| API settings | `src/AuthService.Api/Common/ProxyOptions.cs` | `TrustedProxies`, `TrustedNetworks`, `ForwardLimit`, `Problems()`, `ToForwardedHeadersOptions()` |
| API wiring | `src/AuthService.Api/Program.cs` | Reads the options, throws `InvalidOperationException` with all problems joined, then `UseForwardedHeaders` if `IsConfigured` |
| Web settings | `src/AuthService.Web/Security/ProxyOptions.cs` (`AuthService:Proxy`) | Same, plus `X-Forwarded-Proto` so the secure cookie and scheme are right |
| Web to API | `src/AuthService.Web/Api/ClientAddressHandler.cs` | A `DelegatingHandler` on the `AuthApiClient` that sets `X-Forwarded-For` to the visitor's address |

Startup validation messages (the process refuses to start):

- `Auth:Proxy:TrustedProxies has '<value>', which is not an IP address.`
- `Auth:Proxy:TrustedNetworks has '<value>', which is not a range like 10.0.0.0/8.`
- `Auth:Proxy:ForwardLimit must be between 1 and 10.`

How the web console passes the visitor on (in the shipped stack):

1. Browser connects to Caddy. Caddy replaces any `X-Forwarded-For` the visitor sent with the address it saw (comment in `deploy/Caddyfile`).
2. The web console trusts Caddy because `AuthService__Proxy__TrustedNetworks__0` is the Docker subnet (`DOCKER_SUBNET`, default `172.29.200.0/24`). Its `RemoteIpAddress` becomes the visitor's.
3. Every call the console makes to the API goes through `ClientAddressHandler`, which **removes** any `X-Forwarded-For` and sets one value: the address the console itself worked out (IPv4-mapped IPv6 is shown as IPv4). It never copies a header from the visitor.
4. The API trusts the console because `Auth__Proxy__TrustedNetworks__0` is the same subnet, so the login rate limit and the audit entry see the visitor.
5. Products that call the API through Caddy are identified by the address Caddy saw. Their server-to-server calls (token, validate) are therefore limited per product-server address.

Test coverage: `tests/AuthService.IntegrationTests/Proxy/ForwardedHeadersTests.cs` (trusted proxy attributed to the forwarded client; header
from an unlisted peer ignored; values the client prepended not believed; each client behind a proxy has its own limit; no header means the
proxy's address) and `ForwardedHeadersSettingsTests.cs` (blank settings ignored; bad settings stop startup). Web: `tests/AuthService.Web.Tests/ClientAddressTests.cs`.

### Configuration and environment

| Setting (both forms) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Proxy:TrustedProxies` / `Auth__Proxy__TrustedProxies__0` (`__1`...) | empty | API `ProxyOptions` | Exact proxy addresses believed |
| `Auth:Proxy:TrustedNetworks` / `Auth__Proxy__TrustedNetworks__0` | empty | API `ProxyOptions` | CIDR ranges believed. **Too wide a range lets any host in it choose its own address** |
| `Auth:Proxy:ForwardLimit` / `Auth__Proxy__ForwardLimit` | `1` | API `ProxyOptions` | Number of proxies in the chain. Too high lets a caller prepend entries that are believed |
| `AuthService:Proxy:TrustedProxies`, `TrustedNetworks`, `ForwardLimit` / `AuthService__Proxy__...` | empty, empty, `1` | Web `ProxyOptions` | Same for the web console, which also takes `X-Forwarded-Proto` |

### Failure modes and production debugging

- No HTTP error is returned for a wrong proxy setting: the symptom is wrong data. Per-IP limits fire for everyone (everyone behind one address) or audit `source_ip` shows the proxy.
- **Find the address the API sees.** Sign up once and run `SELECT source_ip FROM audit_entries ORDER BY occurred_at DESC LIMIT 1;`. With Docker on one machine the API
  sees the Docker network gateway, not `127.0.0.1` (`docs/deployment.md`, "Behind a proxy").
- If the proxy is listed but sends no header, the entry shows the proxy's own address (test `ARequestWithNoHeaderFromTheProxyIsAttributedToTheProxy`).
- The API never trusts the header from the internet, so spoofed headers produce the spoofer's real address, not the forged one.
- `/metrics` is restricted by `RemoteIpAddress`, which is the forwarded address when the proxy is listed, so a scraper must reach the service directly (see Health).

Likely causes: proxy range not matching the real peer (Docker gateway differs), `DOCKER_SUBNET` changed in `.env` for the network but the
apps read a different value, or an extra proxy added without raising `ForwardLimit`.

### Decisions and open questions

- **Trust by network, not by header.** The shipped stack trusts the whole Docker subnet for both apps, so any container on that network can set an address. Acceptable for a private compose network; a wider deployment should list individual proxies.
- **The API reads only `X-Forwarded-For`.** It does not read `X-Forwarded-Proto` or host; HTTPS is ended by Caddy and the API does not issue redirects or cookies, so this is not currently needed.
- **Risk:** the web console sends the visitor's address to the API. If the console were exposed directly (not behind Caddy) with no proxy listed, it would send the real peer, which is still correct; if misconfigured with a wide trusted range, a visitor could choose their address.
- **Unknown:** how a deployment with more than one proxy hop between Caddy and the apps should set `ForwardLimit` (the compose stack has exactly one).

---

## Paging

### What it does

Every list endpoint returns one page at a time, never the whole list, with a cursor to continue. This keeps responses small and stable while rows are added or removed between requests.

Rules (`PageQuery` in `src/AuthService.Api/Common/Paging.cs`):

- `limit`: 1 to 200, default 50 (`PageQuery.DefaultLimit`, `MaxLimit`). With no parameters the first 50 are returned.
- Response is `{ "items": [...], "next": ... }`. `next` is `null` on the last page.
- **Name-ordered lists** (members by email, roles, products, permissions, subscriptions by product name) take `after` and `afterId` and return `next: { after, afterId }`. Order is name ascending, then id ascending.
- **Newest-first lists** (pending invitations, API keys) take `before` and `beforeId` and return `next: { before, beforeId }`. Order is time descending, then id descending.
- Pass the cursor back exactly as returned. A cursor holds only a position, never a tenant or a row: a cursor from another tenant starts somewhere in the caller's own rows.
- The order is total (sort key, then id), so no row is skipped or repeated when rows are added between pages.

Which endpoints use which convention:

| Endpoint | Convention | Sorted by |
|---|---|---|
| `GET /api/auth/tenants/me/members` | `after`/`afterId` | email |
| `GET /api/auth/roles` | `after`/`afterId` | name (built-in first) |
| `GET /api/auth/tenants/me/subscriptions` | `after`/`afterId` | product name |
| `GET /api/auth/applications`, `GET /api/auth/users/me/applications` | `after`/`afterId` | product name |
| `GET /api/auth/admin/applications`, `GET /api/auth/admin/applications/{id}/permissions` | `after`/`afterId` | name |
| `GET /api/auth/tenants/me/invitations` | `before`/`beforeId` | created, newest first |
| `GET /api/auth/apikeys` | `before`/`beforeId` | created, newest first |
| `GET /api/auth/tenants/me/audit`, `GET /api/auth/admin/audit` | `before`/`beforeId` | occurred, newest first (own implementation, `AuditPaging`) |
| `GET /api/auth/admin/webhooks` | `before`/`beforeId` | created, newest first (own implementation in `WebhookAdminEndpoints`) |

### How it is implemented

1. Endpoints take `[AsParameters] PageQuery page` and call `page.Problem(byName)`; a non-null result is returned as the response.
2. The service calls `page.ByNameAsync(source, name, id, ct)` or `page.NewestFirstAsync(source, time, id, ct)`.
3. The cursor predicate is built as an expression tree and the cursor values are held in a `CursorValue<T>` object, so the database receives them as parameters and one query plan serves all pages.
4. It reads `limit + 1` rows; if there are more than `limit` the extra proves a next page exists and the cursor is the last returned row.

The web console reads whole lists with `AuthApiClient.GetAllAsync` (`src/AuthService.Web/Api/AuthApiClient.cs`): page size 200, at most 50 pages (10 000 rows), following `next`; any failed page fails the whole read.

### Configuration and environment

None. The limits are constants (`PageQuery.DefaultLimit = 50`, `MaxLimit = 200`; `AuditPaging` and `WebhookAdminEndpoints` repeat the same numbers).

### Failure modes and production debugging

| Request | Response |
|---|---|
| `limit` below 1 or above 200 | `400` validation problem, `errors.limit`: "Limit must be between 1 and 200." |
| Only one of `after`/`afterId` (name-ordered lists) | `400`, `errors.after`: "Pass after and afterId together, exactly as the previous page returned them." |
| `after` empty or longer than 512 characters | `400`, `errors.after`: "That is not a cursor this list returned." |
| Only one of `before`/`beforeId` (newest-first `PageQuery` lists) | `400`, `errors.before`: "Pass before and beforeId together, exactly as the previous page returned them." |
| Malformed `afterId` or `beforeId` (not a GUID) or `before` (not a date) | **Assumption (confirm):** `400` from ASP.NET Core's own parameter binding; not covered by a test I read |

Known edge cases:

- Audit and webhook lists accept `before` **without** `beforeId`; this filters by time only and may skip rows that share the same timestamp.
- Name ordering compares strings in the database query (`string.Compare` in the expression). **Assumption (confirm):** the order therefore follows PostgreSQL's collation for the column, which can differ between the Alpine test image and a Debian-based production server (ADR 0001 warns about this).
- A client that reads only the first response sees at most 50 rows and must follow `next`. A ticket "the list shows only 50" is almost always a client that ignores `next`.

### Decisions and open questions

- Keyset (cursor) paging instead of offset paging, for stable results while data changes.
- **Inconsistency:** three paging implementations exist (`PageQuery`, `AuditPaging`, `WebhookAdminEndpoints.ListAsync`) with slightly different validation (see edge cases). **Unknown:** whether `AuditPaging` and the webhook list are meant to move onto `PageQuery`.
- The web console's cap (50 pages of 200) silently truncates a list beyond 10 000 rows because the loop ends without an error. **Assumption (confirm):** no tenant is expected to reach that size.

---

## Health, readiness and metrics

### What it does

Three unauthenticated endpoints let the platform and the operator see whether the service is alive, can serve traffic, and how it is behaving. Probes cannot send a login, so none requires one. The two health endpoints say only "ok" or "failed"; `/metrics` is limited to the operator's networks.

### How it is implemented

| Verb and route | Auth | Handler | What it does |
|---|---|---|---|
| `GET /health/live` | anonymous | `ObservabilityEndpoints.GetLive` | `200 {"status":"alive"}`. Touches nothing, so a database outage never restarts the service |
| `GET /health/ready` | anonymous | `ObservabilityEndpoints.GetReadyAsync` -> `ReadinessService.CheckAsync` | `200 {"status":"ready","checks":{"database":"ok","signingKey":"ok"}}` or `503 {"status":"unavailable", ...}` with `failed` for the failing check |
| `GET /metrics` | anonymous, plus `InternalNetworkFilter` | `ObservabilityEndpoints.GetMetricsAsync` | Prometheus text (`text/plain; version=0.0.4`). Anyone outside the allowed networks gets a plain `404` |

All three send `Cache-Control: no-store`.

Readiness checks (`ReadinessService`):

- **database**: `db.Database.GetPendingMigrationsAsync` with a 5-second timeout. Fails if the database cannot be reached or if any migration is pending. Logs `Not ready: the database is missing {Count} migrations.` or `Not ready: the database could not be reached.`
- **signingKey**: `SigningKeyService.HasCurrentKey`. Logs `Not ready: there is no signing key that signs right now.`
- The response never says why (no connection string, no error text).

Metrics (`AuthMetrics` in memory since process start; `ObservabilityEndpoints` for the database-backed gauges). Labels are fixed words only: route template, status code, failure reason.

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `auth_http_requests_total` | counter | `method`, `route`, `status` | Requests answered. Unknown methods are `OTHER`; unmatched routes are `unmatched` |
| `auth_http_request_duration_seconds` | histogram | `method`, `route` | Buckets 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, `+Inf` |
| `auth_login_failures_total` | counter | `reason` | Refused logins: `wrong_password`, `unknown_account`, `account_locked`, `account_disabled`, `email_not_verified` |
| `auth_email_outbox_messages` | gauge | `status` = `pending`, `failed` | Emails waiting or given up on |
| `auth_email_outbox_oldest_pending_age_seconds` | gauge | none | Age of the oldest unsent email |
| `auth_webhook_outbox_messages` | gauge | `status` = `pending`, `failed` | Same for webhooks |
| `auth_webhook_outbox_oldest_pending_age_seconds` | gauge | none | Same |
| `auth_signing_keys_published` | gauge | none | Keys in the JWKS right now |
| `auth_signing_key_age_seconds` | gauge | none | Age of the key that signs now (omitted when there is none) |
| `auth_process_start_time_seconds` | gauge | none | Process start, Unix time |

`RequestMetricsMiddleware` is placed before the exception handler so a request that ends in an error is counted as `500`. If the outbox gauges cannot be read, the rest of the metrics are still sent and `The outbox metrics could not be read from the database.` is logged.

Counters are per instance and restart at zero with the process.

**Who may read metrics.** `ObservabilityOptions.IsAllowed`: loopback, plus the CIDR ranges in `Observability:AllowedNetworks`. An address of `null` is never allowed; IPv4-mapped IPv6 is mapped first. The address checked is `Connection.RemoteIpAddress`, which is the forwarded client address when `Auth:Proxy` is set. Caddy additionally answers `404` for `/metrics` from the internet (`deploy/Caddyfile`).

### Configuration and environment

| Setting (both forms) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Observability:AllowedNetworks` / `Observability__AllowedNetworks__0` | empty (loopback only) | `ObservabilityOptions` | Adds networks that may scrape. A bad CIDR stops startup: `Observability:AllowedNetworks has '<x>', which is not a range like 10.0.0.0/8.` Blank entries are ignored. Compose passes `METRICS_ALLOWED_NETWORK` |

### Failure modes and production debugging

- `/health/ready` `503` with `database: failed`: database down, wrong connection string, or **a release started before its migration** (stays `503` until the migrate job has run). Check the log for `Not ready:`.
- `503` with `signingKey: failed`: no key signs now (keys not loaded because the database was unreachable at refresh, or the key table is empty after an interrupted first start).
- `/metrics` returns `404` from the monitoring server: its address is not in `Observability:AllowedNetworks`, or a proxy is listed so the address the API sees is the client's (point the scraper at the service directly and list the scraper's network; with a published Docker port the scraper may arrive from the bridge gateway).
- The compose health check calls `/health/ready` every 15 s and Caddy waits for the API to be healthy (`depends_on ... service_healthy`), so a failing readiness keeps the whole stack from serving.
- `deploy/check.sh` verifies readiness, JWKS, that `/metrics` returns 404 from the internet, and the web console.

Useful alerts (from `docs/deployment.md`): readiness failing for more than a minute; `auth_email_outbox_messages{status="failed"} > 0`; `auth_webhook_outbox_messages{status="failed"} > 0`; `auth_email_outbox_oldest_pending_age_seconds` above a few minutes; a burst of `auth_login_failures_total`; `5xx` in `auth_http_requests_total`.

### Decisions and open questions

- Liveness is deliberately independent of the database; readiness is deliberately strict about migrations (migrations are never applied on startup, ADR 0003).
- Metrics are hand-written Prometheus text, not a metrics library; there is no process or runtime metric beyond start time.
- **Gap:** readiness does not check the email provider, the workers, or the rate-limit table separately (the table is covered indirectly by "no pending migration"). A stopped worker is visible only through the outbox gauges.
- **Gap:** no metric for rate-limit refusals, other than `auth_http_requests_total` with status `429` or `503`.

---

## Error responses

### What it does

Every error answers as RFC 7807 problem JSON (`application/problem+json`) with a human-readable `title` and `detail`. Business failures
also carry a stable machine-readable `code` extension, so clients can tell failures apart without parsing text.

### How it is implemented

- `ApiProblem.Create(statusCode, code, title, detail)` (`src/AuthService.Api/Common/ApiProblem.cs`) builds the response with `Results.Problem` and adds `extensions.code`.
- `Results.ValidationProblem(errors)` is used for input errors: `400` with `errors: { "<field>": ["message"] }` and no `code`.
- `app.UseExceptionHandler()` with `AddProblemDetails()` turns any unhandled exception into a plain `500` problem response, never a stack trace (comment in `Program.cs`). The exception is logged by the framework. No `code`.
- A missing, invalid or expired bearer token: the JWT `OnChallenge` event (in `IdentityAuthentication`) answers `401`, `WWW-Authenticate: Bearer`, code `unauthorized`, detail `A valid access token is required.`
- Signup's duplicate email is written with `Results.Problem` directly (code `email_already_registered`, `409`).

The web console has its own `ApiProblem` type (`src/AuthService.Web/Api/ApiProblem.cs`) that reads these responses.

#### Stable `code` values in use

Collected from every `ApiProblem.Create` call (and the signup `Results.Problem`). Status is what the endpoint returns.

| `code` | Status | Meaning | Where |
|---|---|---|---|
| `unauthorized` | 401 | No valid access token | JWT challenge; a few handlers |
| `invalid_credentials` | 401 | Wrong email or password, unknown account, or locked (same answer on purpose) | login |
| `account_disabled` | 403 | The account is disabled | login, refresh, create tenant |
| `email_not_verified` | 403 | Verify the email first | login |
| `invalid_refresh_token` | 401 | Unknown, expired, revoked or reused refresh token | refresh |
| `invalid_client` | 401 | Unknown client, wrong or expired secret | `POST /api/auth/token` |
| `invalid_request` | 400 | `grant_type`, `client_id` or `client_secret` missing | `POST /api/auth/token` |
| `unsupported_grant_type` | 400 | Only `client_credentials` is supported | `POST /api/auth/token` |
| `invalid_api_key` | 401 | The API key is not valid | `POST /api/auth/apikeys/validate` |
| `subscription_not_active` | 403 | The key's tenant has no active subscription to the product | API key validation |
| `outside_key_scope` | 403 | The key does not allow that action | API key validation |
| `scope_exceeds_permissions` | 403 | A new key cannot have more than its creator | API key create |
| `api_key_not_active` | 409 | A revoked or expired key cannot be rotated | API key rotate |
| `email_already_registered` | 409 | Email already has an account; log in and create a tenant instead | signup |
| `verification_link_invalid` | 410 | Unknown, expired or used verification link | email verify |
| `reset_link_invalid` | 410 | Unknown, expired or used reset link | password reset |
| `invitation_invalid` | 410 | Invitation unknown, expired, used or withdrawn | accept invitation |
| `invitation_not_pending` | 409 | Already accepted, or was not invited | invitation resend, invitation withdraw |
| `resend_too_soon` | 429 | Invitation emailed a moment ago (with `Retry-After`) | invitation resend |
| `login_required` | 401 | The email already has an account: log in, then accept | accept invitation |
| `wrong_account` | 403 | Invitation is for a different email | accept invitation |
| `password_not_allowed` | 400 | Existing account must not set a password when accepting | accept invitation |
| `already_member` | 409 | The email already belongs to this tenant | invite |
| `owner_role_restricted` | 403 | Only an Owner can invite, give, remove or disable the Owner role | invite, members, roles |
| `owner_role_immutable` | 403 | The built-in Owner role can never be edited or deleted | roles |
| `last_owner` | 409 | Would leave the tenant without an active Owner | members, roles |
| `roles_required` | 409 | A member must keep at least one role | assign roles |
| `role_name_taken` | 409 | Role name already used in the tenant | create/update role |
| `role_in_use` | 409 | Members still hold the role | delete role |
| `membership_invited` | 409 | Person has not accepted the invitation yet | member status |
| `membership_disabled` | 409 | A disabled member cannot be given access | grant access |
| `no_seat_available` | 409 | Seat limit reached | invite, enable member, grant access, resend an expired invitation |
| `application_not_subscribed` | 400 | The tenant has no active subscription to the product | invite, grant access |
| `tenant_access_denied` | 403 | Not an active member of the organisation | my applications |
| `tenant_suspended` | 403 (my applications, token exchange), 409 (subscribe) | Organisation suspended | several |
| `tenant_not_found` | 404 | No such tenant | subscribe, tenant profile |
| `exchange_denied` | 403 | No access to this application in this tenant | token exchange |
| `application_not_found` | 404 | No application has this id | platform admin |
| `application_key_taken` | 409 | Product key already registered | platform admin |
| `audience_taken` | 409 | JWT audience already used by another product | platform admin |
| `no_permissions_registered` | 409 | Register permissions before activating | platform admin |
| `application_not_active` | 400 | Tenants cannot subscribe to an inactive product | subscribe |
| `subscription_not_found` | 404 | No such subscription | subscriptions |
| `subscription_cancelled` | 409 | A cancelled subscription cannot be suspended | subscriptions |
| `webhook_not_found` | 404 | No webhook has this id | platform admin |
| `webhook_not_failed` | 409 | Only a failed webhook can be sent again | platform admin |
| `not_found` | 404 | Generic: no such API key, invitation, member, role in this tenant | several |
| `rate_limited` | 429 | Limit exceeded (with `Retry-After`) | limited endpoints |
| `rate_limit_unavailable` | 503 | Counters unreachable; refused (with `Retry-After: 5`) | limited endpoints |

### Configuration and environment

None. Problem responses do not depend on settings.

### Failure modes and production debugging

- `4xx` with a `code`: a business rule, not a fault. Search the code for the quoted string to find the producing method.
- `400` without a `code` and with `errors`: input validation. The field name is the key.
- `500` with a generic body: read the API log at the time (the stack trace is only there). Typical causes: a missing migration, a database error, or a rejected audit `detail`.
- `403` from an authorization policy (a valid token that lacks the permission or the right audience) has **no body and no `code`**: only the JWT challenge was customised, not the forbidden result. **Assumption (confirm):** the default ASP.NET Core forbidden response (empty body) is used; I saw no `OnForbidden` handler in `IdentityAuthentication.cs`.
- An unmatched route returns the framework's plain `404` (and is counted as `unmatched` in metrics).

### Decisions and open questions

- Identical answers for different causes (login, refresh, verification, reset, invitation, client credentials) on purpose, so responses reveal nothing about which part failed.
- **Inconsistency:** `tenant_suspended` is `403` in two places and `409` in subscribe; `not_found` is generic where other lookups have specific codes (`application_not_found`, `webhook_not_found`).
- **Inconsistency:** `resend_too_soon` is a `429` that is not produced by the rate limiter (no `rate_limited` code); clients must handle both.
- **Gap:** `403` from policies has no `code`, unlike every other error. **Unknown:** whether a uniform `forbidden` code is planned.

---

## Authorization policies and the endpoint inventory

### What it does

Every endpoint is either on a short reviewed list of anonymous endpoints or carries an explicit authorization rule. A new endpoint without a
decision fails the test `TOK04_EveryEndpointIsEitherOnTheAnonymousListOrExplicitlyProtected`.

### How it is implemented

The policies are registered in `IdentityAuthentication.AddIdentityTokenAuthentication` (`src/AuthService.Api/Tokens/IdentityAuthentication.cs`):

| Policy | Constant | Requires |
|---|---|---|
| Identity token | `IdentityAuthentication.PolicyName` = `IdentityToken` | Authenticated user with a `sub` claim and **no `aud` claim** (a product token must not work on identity endpoints) |
| Platform admin | `IdentityAuthentication.PlatformAdminPolicyName` = `PlatformAdmin` | Identity token **and** `users.is_platform_admin = true`, read from the database on every request by `PlatformAdminHandler` (not a claim, so revocation is immediate) |
| Service token | `ServicePolicyName` = `ServiceToken` | Claim `token_use = service`, `aud` equal to `IdentityTokenService.ServiceAudience`, and an `app_id` claim |
| Tenant context | `TenantAuthorization.TenantContext` (inline policy) | Authenticated, `aud = auth-service-tenant` (`BuiltInApplication.Audience`), a `tid` tenant claim and a `sub` |
| Tenant permission | `RequireTenantPermission(key, level)` (inline) | Tenant context **and** a `permissions` claim `key:Level` at or above the level (`PermissionHandler`). Never reads role names |

Token validation (`JwtBearer`): ES256 only, issuer = `Auth:Tokens:Issuer`, signature key chosen by `kid` among published keys, lifetime
validated with 30 seconds of clock skew, audience not validated by the bearer handler (the policies check it).

The tenant of a tenant-scoped request is always the token's `tid`, never a route or query value. The only endpoints that take a tenant from
the route are the ones a product calls with its service token (`/api/auth/tenants/{tenantId}/subscriptions` and the routes under it); the test enforces this.

Everything under `/api/auth/admin` is mapped through `AdminEndpoints.MapAdminGroup`, which applies the `PlatformAdmin` policy to the whole group.

#### Endpoint inventory

**Anonymous** (exactly the list in `Tok04/EndpointInventoryTests.AnonymousEndpoints`):

| Endpoint | Notes |
|---|---|
| `GET /` | Returns `AuthService` |
| `GET /.well-known/auth-service-configuration` | The issuer and the public addresses a product starts from (ONB-03) |
| `GET /.well-known/jwks.json` | Public signing keys |
| `GET /health/live`, `GET /health/ready` | Probes |
| `GET /metrics` | Anonymous in policy terms, restricted by network filter |
| `POST /api/auth/signup` | |
| `POST /api/auth/login` | Rate-limited (`login`) |
| `POST /api/auth/refresh`, `POST /api/auth/logout` | Authenticated by the refresh token in the body |
| `POST /api/auth/email/verify` | |
| `POST /api/auth/email/verify/resend` | Rate-limited (`email-verification-resend`) |
| `POST /api/auth/password/forgot`, `POST /api/auth/password/reset` | Rate-limited (`password-reset`) |
| `POST /api/auth/invitations/accept` | Reads an identity token itself if the invitee already has an account |
| `POST /api/auth/token` | Product client-credentials login; rate-limited (`service-token`) |

**Identity token** (`IdentityToken`): `GET /api/auth/users/me`, `POST /api/auth/tenants` (create another tenant), `GET /api/auth/applications`,
`GET /api/auth/users/me/applications`, `POST /api/auth/token/exchange` (also rate-limited per person).

**Service token** (`ServiceToken`, a product's machine token): `GET /api/auth/applications/me`, `PUT` and `GET /api/auth/applications/me/webhook`,
`POST /api/auth/tenants/{tenantId}/subscriptions`, `PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}/seats`,
`PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}`, `POST /api/auth/apikeys/validate`.

**Platform admin** (`PlatformAdmin`, all under `/api/auth/admin`): `GET` and `POST /applications`, `GET` and `POST /applications/{id}/permissions`,
`PUT /applications/{id}/launch-url`, `PUT /applications/{id}/active`, `POST /applications/{id}/secret/rotate`, `POST /signing-keys/rotate`,
`GET /audit`, `GET /webhooks`, `POST /webhooks/{webhookId}/resend`.

**Tenant context only** (read-only; any member's tenant token): `GET /api/auth/tenant/me`.

**Tenant permission** (tenant token with the permission at the level; permission keys from `PermissionKeys` in `BuiltInApplication.cs`):

| Endpoint | Permission and level |
|---|---|
| `GET /api/auth/tenants/me/audit` | `audit.read` ReadOnly |
| `GET /api/auth/tenants/me/members` | `members.read` ReadOnly |
| `PATCH /api/auth/tenants/me/members/{membershipId}` | `members.manage` Full |
| `POST /api/auth/tenants/me/invitations`, `POST .../invitations/{membershipId}/resend`, `DELETE .../invitations/{membershipId}` | `members.invite` Full |
| `GET /api/auth/tenants/me/invitations` | `members.read` ReadOnly |
| `PUT /api/auth/tenants/me/members/{membershipId}/roles` | `roles.assign` Full |
| `PUT` and `DELETE /api/auth/tenants/me/members/{membershipId}/applications/{applicationId}` | `access.manage` Full |
| `GET /api/auth/tenants/me/subscriptions` | `subscriptions.read` ReadOnly |
| `GET /api/auth/tenants/me/permissions` | `roles.read` ReadOnly |
| `POST /api/auth/roles`, `PATCH` and `DELETE /api/auth/roles/{roleId}` | `roles.manage` Full |
| `GET /api/auth/roles` | `roles.read` ReadOnly |
| `POST /api/auth/apikeys`, `DELETE /api/auth/apikeys/{keyId}`, `POST /api/auth/apikeys/{keyId}/rotate` | `apikeys.manage` Full |
| `GET /api/auth/apikeys`, `GET /api/auth/apikeys/{keyId}` | `apikeys.read` ReadOnly |

Cross-checks enforced by `Tok04/EndpointInventoryTests`: every tenant-scoped route (`/api/auth/tenants/me*`, `/api/auth/tenant/me`, `/api/auth/roles*`, `/api/auth/apikeys*`
except `/validate`) needs a tenant token; no tenant-scoped route has `{tenantId}` in it; any tenant-context endpoint that is not `GET`
needs a permission; every `/api/auth/admin/...` endpoint is `PlatformAdmin` and no other endpoint is.

### Configuration and environment

| Setting (both forms) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:Issuer` / `Auth__Tokens__Issuer` | `AuthService` | `IdentityTokenOptions` | The accepted `iss`; products must use the same value |
| `Auth:Tokens:AccessTokenMinutes`, `ServiceTokenMinutes`, `ProductTokenMinutes` | `15`, `10`, `15` | `IdentityTokenOptions` | Token lifetimes (validated with 30 s skew) |

### Failure modes and production debugging

- `401` `unauthorized`: token missing, expired, wrong issuer, unknown or retired `kid`, not ES256.
- `403` (empty body): the token is valid but the policy fails: a product token on an identity endpoint (has `aud`), an identity token on a tenant endpoint (no `aud = auth-service-tenant`), a tenant token missing the permission or at a lower level, a non-admin on `/api/auth/admin`, a service token that is not marked `token_use = service`.
- A platform admin who was just granted the flag must **log in again**: the flag is read from the database on each request, but the setup note in `docs/deployment.md` says to log in again for a fresh token.
- The first platform admin is made by SQL only: `UPDATE users SET is_platform_admin = true WHERE email = '...';`.
- Tokens are checked locally, with no database call (apart from the admin flag), so a revoked refresh token does not invalidate an access token before it expires.

### Decisions and open questions

- Platform-admin status is a database flag, not a claim, so revoking is immediate and no token can carry it.
- Tenant authorization never reads role names, only permission claims.
- **Unknown:** the inventory above was assembled from `Map*` calls and the test's classification; run `Tok04/EndpointInventoryTests` when adding endpoints and compare the anonymous list there.

---

## Database, migrations and background workers

### What it does

All state lives in one PostgreSQL 17 database (ADR 0001). The schema comes only from EF Core migrations, which are applied explicitly by a separate job and never by the API at startup (ADR 0003).

Rules:

- **Table and column names are snake_case** (`EFCore.NamingConventions`, `UseSnakeCaseNamingConvention` in `AuthDbContextRegistration.ConfigureAuthDb`).
- **`citext`** (case-insensitive text) backs email columns (`modelBuilder.HasPostgresExtension("citext")`). The database role must be able to create it; some managed services restrict it (ADR 0001).
- **Migrations are never applied on startup.** `EnsureCreated()` is never used. `Program.cs` says so in its first comment.
- **Deleting is restricted:** every foreign key is `DeleteBehavior.Restrict`, so rows are not removed by cascade.
- The connection string is read lazily (`AddAuthDbContext(serviceProvider => ...GetConnectionString("AuthDb"))`) so overrides added after startup still apply; missing it throws `Connection string 'AuthDb' is not configured.`

### How it is implemented

#### Migration policy

- Add a migration: `dotnet ef migrations add <Name> -p src/AuthService.Infrastructure -s src/AuthService.Api -o Persistence/Migrations`. The design-time factory (`AuthDbContextDesignTimeFactory`) uses `AUTH_DB_DESIGN_CONNECTION` or a fallback local string and never connects for `migrations add`.
- Test `TST_TC2_ModelHasNoChangesMissingAMigration` fails if the model changes without a migration; `TST_TC1` checks none is pending.
- Production: the `migrate` image (`Dockerfile` target `migrate`) is a self-contained `efbundle` that applies every pending migration: `authservice-migrate --connection "<connection string>"`. In compose it runs once, and the API starts only after it completes successfully (`depends_on: migrate: condition: service_completed_successfully`). It takes a database lock, so two runs take turns, and it is safe to run again.
- Migrations must stay compatible with the version still serving ("add before you remove"). A migration is not undone by going back to an older version.
- A new version started before its migration shows `/health/ready` as `503` and rate-limited endpoints as `503 rate_limit_unavailable`.
- On startup the API does, in order (`Program.cs`): load or create the first signing key, `BuiltInApplicationSync.SyncAsync` (stores the Auth Service's own product and permissions under an advisory lock), construct the email sender (fails fast on bad settings), validate proxy, observability and rate-limit settings. A database that is unreachable, or that lacks the `signing_keys` table, makes the API throw at the first step and exit; a database that is only missing a later migration (for example `rate_limit_counters`) starts, and then fails `/health/ready` and the endpoints that need the missing table.

Migrations, by name (oldest first): `Baseline`, `AddIdentityAndTenancy`, `AddRefreshTokens`, `AddEmailOutbox`, `AddEmailVerificationTokens`, `AddLoginLockout`,
`AddPasswordResetTokens`, `AddSigningKeys`, `AddAuditLog`, `AddPlatformAdminFlag`, `AddApplications`, `AddPermissions`, `AddSubscriptions`,
`AddBuiltInApplications`, `AddRolePermissions`, `AuditDetailUpTo128`, `AddInvitations`, `AddWebhookDelivery`, `AddApiKeys`, `AddInvitationLastSentAt`,
`AddApplicationLaunchUrl`, `AddWebhookPreviousSecret`, `AddRateLimitCounters`. Three contain hand-written SQL: `AddAuditLog` (the append-only triggers),
and `AddWebhookDelivery` and `AddInvitationLastSentAt` (data back-fills). The history table is `__EFMigrationsHistory` (column `migration_id`).

#### Data model overview

Tables (entity class in `src/AuthService.Infrastructure/Persistence/Entities`). All keys are `uuid` (version 7 where generated by the service) unless noted.

| Group | Table | Entity | Relationships |
|---|---|---|---|
| Identity | `users` | `User` | Global identity: `email` (unique, `citext`), `password_hash`, `display_name`, `email_verified_at`, `failed_login_count`, `locked_until`, `is_platform_admin` |
| Tenancy | `tenants` | `Tenant` | An organisation: `status` |
| Tenancy | `tenant_memberships` | `TenantMembership` | `tenant_id` -> tenants, `user_id` -> users; unique `(tenant_id, user_id)`; `status` |
| Roles | `roles` | `Role` | `tenant_id` -> tenants; unique `(tenant_id, name)`; `is_built_in` |
| Roles | `role_permissions` | `RolePermission` | `(tenant_id, role_id)` -> roles, `permission_id` -> permissions; `level` |
| Roles | `membership_roles` | `MembershipRole` | `(tenant_id, membership_id)` -> memberships, `(tenant_id, role_id)` -> roles; unique `(membership_id, role_id)` |
| Products | `applications` | `Application` | Product: unique `key`, `jwt_audience`, `client_id`; `is_active`, `is_built_in`, webhook and launch URL fields |
| Products | `application_secrets` | `ApplicationSecret` | `application_id` -> applications; hashed client secrets with `expires_at` |
| Products | `permissions` | `Permission` | `application_id` -> applications; unique `(application_id, key)` |
| Products | `application_role_templates` | `ApplicationRoleTemplate` | `application_id` -> applications; unique `(application_id, name)`; `description` at most 500 (ONB-04) |
| Products | `application_role_template_permissions` | `ApplicationRoleTemplatePermission` | `template_id` -> templates, `permission_id` -> permissions; unique `(template_id, permission_id)`; `level` as text |
| Products | `subscriptions` | `TenantApplicationSubscription` | `tenant_id`, `application_id`; unique pair; `status`, `max_seats` (check: null or at least 1) |
| Products | `application_access` | `MembershipApplicationAccess` | `(tenant_id, membership_id)`, `application_id`; unique `(membership_id, application_id)` |
| Invitations | `invitations` | `Invitation` | `(tenant_id, membership_id)` -> memberships, `invited_by_user_id` -> users; `token_hash` unique; `expires_at`, `used_at`, `last_sent_at` |
| API keys | `api_keys` | `ApiKey` | `tenant_id`, optional `application_id`, `created_by_user_id`; unique `key_id`; `expires_at`, `revoked_at` |
| API keys | `api_key_permissions` | `ApiKeyPermission` | `(tenant_id, api_key_id)`, `permission_id`; `level` |
| Sessions and links | `refresh_tokens` | `RefreshToken` | `user_id`; unique `token_hash`; `family_id` (reuse detection); `revoked_at` |
| Sessions and links | `email_verification_tokens`, `password_reset_tokens` | `EmailVerificationToken`, `PasswordResetToken` | `user_id`; unique `token_hash`; `expires_at`, `used_at` |
| Keys | `signing_keys` | `SigningKey` | Encrypted private key; `publish_from`, `sign_from`, `sign_until`, `publish_until`; unique `key_id` |
| Queues | `email_outbox` | `EmailOutboxMessage` | See Email delivery |
| Queues | `webhook_outbox` | `WebhookOutboxMessage` | `application_id` -> applications; `status`, `attempts`, `next_attempt_at`, `deadline_at`; index `(status, next_attempt_at)` |
| Platform | `audit_entries` | `AuditEntry` | See Audit log; no foreign keys |
| Platform | `rate_limit_counters` | `RateLimitCounter` | See Rate limiting; no foreign keys |

Tokens (refresh, verification, reset, invitation) are stored only as hashes (`token_hash`, `SecretToken.Hash`); the raw value is returned or emailed once.

#### Hosted background workers

Five `BackgroundService` classes are registered in `Program.cs`. All run in every API instance; instances take turns using database locks (`FOR UPDATE SKIP LOCKED` for the outboxes, an advisory lock for key creation).

| Worker | Class and file | Interval setting (default) | What it does | Effect if stopped |
|---|---|---|---|---|
| Signing key | `SigningKeyWorker` (`Tokens/SigningKeyWorker.cs`) | `Auth:Tokens:SigningKeyRefreshSeconds` (60) | Reloads keys from the database and, when due, schedules the next key (`SigningKeyRotation.EnsureAsync`) | This instance stops learning new keys; no rotation is scheduled. When the in-memory current key's `sign_until` passes without the next key loaded, readiness fails (`signingKey: failed`) and tokens cannot be issued |
| Email | `EmailDispatcher` (`Email/EmailDispatcher.cs`) | `Email:DispatchIntervalMilliseconds` (2000); `Email:DispatcherEnabled` (true) | Sends due emails, up to 50 per pass | Emails stay `Pending`: no verification, reset or invitation links |
| Webhooks | `WebhookDispatcher` (`Features/Webhooks/WebhookDispatcher.cs`) | `Auth:Webhooks:DispatchIntervalMilliseconds` (2000); `Auth:Webhooks:DispatcherEnabled` (true) | Delivers due webhook events, up to 50 per pass | Products are not told about subscription changes; `webhook_outbox` fills; the 24-hour deadline can pass and events become `Failed` |
| Audit retention | `AuditRetentionWorker` (`Features/Audit/AuditRetention.cs`) | `Auth:Audit:RetentionCheckSeconds` (86 400); `Auth:Audit:RetentionEnabled` (true) | Deletes entries older than `Auth:Audit:RetentionDays` (365) | `audit_entries` grows without bound |
| Rate-limit cleanup | `RateLimitCleanupWorker` (`RateLimiting/SharedRateLimit.cs`) | `Auth:RateLimit:CleanupSeconds` (60) | Deletes counters expired more than 1 minute ago | `rate_limit_counters` grows; limits still correct |

Only the email and webhook dispatchers and the audit worker have an on/off setting. `SigningKeyWorker` and `RateLimitCleanupWorker` cannot be disabled by configuration.

**No worker removes** expired `refresh_tokens`, `email_verification_tokens`, `password_reset_tokens`, expired `invitations`, `Sent`/`Failed` `email_outbox` rows or `Delivered` `webhook_outbox` rows. I found no `ExecuteDelete` or `Remove` for them outside the audit job and role/access removal.

### Configuration and environment

| Setting (both forms) | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `ConnectionStrings:AuthDb` / `ConnectionStrings__AuthDb` | none (Development: `Host=localhost;Port=5432;Database=auth;Username=postgres;Password=postgres` in `appsettings.Development.json`) | `Program.cs` | **Required.** Missing: `Connection string 'AuthDb' is not configured.` at first use. Compose appends `GSS Encryption Mode=Disable` to silence a harmless library message |
| `AUTH_DB_DESIGN_CONNECTION` (environment only) | `Host=localhost;Port=5432;Database=auth_design;...` | `AuthDbContextDesignTimeFactory` | Used only by `dotnet ef` |
| `Auth:Tokens:KeyEncryptionKey` / `Auth__Tokens__KeyEncryptionKey` | none | `IdentityTokenOptions` | **Secret, required outside Development and Testing**: 32 random bytes in base64. Used to encrypt signing keys (and webhook secrets via `KeyEncryption`). Losing it makes stored keys unreadable |
| `--connection` argument of the migrate image | none | `Dockerfile` target `migrate` | The connection string the migration bundle uses |

The full settings list is in [07 Configuration reference](07-configuration-reference.md). How settings reach the container: `deploy/docker-compose.yml`
passes each as an environment variable from `deploy/.env` (for example `ConnectionStrings__AuthDb` is built from `POSTGRES_PASSWORD`).

### Failure modes and production debugging

- API container restarts in a loop: read the first exception. Common: `Connection string 'AuthDb' is not configured.`; `Auth:Tokens:KeyEncryptionKey is not configured.`; relation does not exist (migration not applied); `Email:...` settings; `Auth:Proxy`, `Observability` or `Auth:RateLimit` settings.
- Migrate job failed: the API never starts (compose `depends_on`). Check `docker compose logs migrate`; a `citext` permission error is the usual first-deploy cause.
- `/health/ready` `503` after a deploy: migrations pending. Query `SELECT migration_id FROM "__EFMigrationsHistory" ORDER BY migration_id DESC LIMIT 5;` and compare with the migration files.
- Key encryption key changed: every start fails when decrypting the stored signing keys. Restore the previous value.
- Database growth: see "No worker removes". Useful read-only sizes: `SELECT relname, n_live_tup FROM pg_stat_user_tables ORDER BY n_live_tup DESC;`

### Decisions and open questions

- PostgreSQL only, with `citext` (ADR 0001). The test image `postgres:17-alpine` is pinned in `tests/AuthService.IntegrationTests/Infrastructure/TestSettings.cs` and must match production.
- Explicit migrations (ADR 0003): application credentials need not have schema rights; no startup race between instances.
- **Risk: unbounded growth** of the tables listed above. **Unknown:** the intended cleanup policy for expired tokens and old outbox rows.
- **Risk: the key-encryption secret protects both signing keys and webhook secrets**, and rotating it is not built (README: "Re-encrypting them under a new secret is not built yet").
- **Gap:** no automated restore test or backup of the key-encryption secret; `deploy/backup.sh` dumps the database only.

---

## Symptom to where to look (this part)

| Symptom | Likely cause | First place to look | What to check |
|---|---|---|---|
| Emails (verification, reset, invitation) are not arriving | Worker not running, provider refusing, or `Failed` rows | `src/AuthService.Api/Email/EmailDeliveryService.cs`; table `email_outbox` | `SELECT status, count(*), min(created_at) FROM email_outbox GROUP BY 1;` then `last_error`; logs `attempt ... failed, will retry` and `failed for good`; metric `auth_email_outbox_messages` |
| Email arrives but the link opens a dead page | `Email:PublicBaseUrl` points at the wrong site (with Brevo it must be absolute https) | `EmailOptions.PublicBaseUrl`, `EmailTemplates.Render` | Open the link; check `Email__PublicBaseUrl`. |
| Everyone gets `429` `rate_limited` on login | Proxy not trusted, all visitors share one address | `Auth:Proxy` in `deploy/docker-compose.yml`, `ClientAddressHandler` | `SELECT source_ip, count(*) FROM audit_entries GROUP BY 1 ORDER BY 2 DESC LIMIT 5;` and the top rows of `rate_limit_counters` |
| Login (or reset, resend, service token, exchange) answers `503` `rate_limit_unavailable` / "Temporarily unavailable" | Counter store unreachable or `rate_limit_counters` missing (migration not run) | `SharedRateLimitMiddleware`; `/health/ready` | Log `The rate-limit store could not be reached ... ({Policy})`; `\d rate_limit_counters`; pending migrations |
| `/health/ready` returns `503` | `database: failed` (unreachable or migrations pending) or `signingKey: failed` | `ReadinessService` | Logs `Not ready: ...`; `__EFMigrationsHistory`; `SELECT * FROM signing_keys;` |
| `/health/ready` is `200` but `/metrics` is `404` for the monitoring server | Scraper's address is not in `Observability:AllowedNetworks`, or a proxy is listed so the scraper appears as a client | `ObservabilityOptions.IsAllowed` | The address the API sees (audit `source_ip` of a request from the scraper); the CIDR setting |
| Audit entries are missing | Event is not audited, the action rolled back, or the write was rejected | `AuditActions`, `audit.Record` call site | `SELECT action, count(*) FROM audit_entries GROUP BY 1;`; look for a `500` at that time |
| Audit entries show the wrong IP (the proxy's) | `Auth:Proxy` missing or wrong range | `Program.cs` forwarded-headers block; `ProxyOptions` | `SELECT source_ip FROM audit_entries ORDER BY occurred_at DESC LIMIT 5;` |
| `actorName` is `null` for a person | Tenant admin reading someone with no membership in the tenant, or system/anonymous actor | `AuditPaging.ActorNamesAsync` | `tenant_memberships` for that user and tenant; `actor_type` |
| A worker does not seem to run | Disabled by setting, or crashed in a loop | `EmailDispatcher`, `WebhookDispatcher`, `AuditRetentionWorker`, `RateLimitCleanupWorker`, `SigningKeyWorker` | Log lines `... is disabled`, the `...failed; trying again` errors; effect column in the worker table |
| A list endpoint returns only 50 rows | Default `limit` is 50; client ignores `next` | `PageQuery` in `Common/Paging.cs` | Look for `next` in the response; pass `limit` up to 200 and the cursor |
| List endpoint returns `400` with `errors.after` or `errors.before` | Half a cursor, or a cursor not returned by the list | `PageQuery.Problem` | Send both values exactly as returned |
| Tenant admin gets `403` with no body on `/api/auth/tenants/me/audit` | Token lacks `audit.read`, or is not a token for the Auth Service's own product | `TenantAuthorization.TenantPermission`; role grants | Decode the token: `aud` must be `auth-service-tenant`; `permissions` must include `audit.read:ReadOnly` or higher |
| Platform admin gets `403` on `/api/auth/admin/*` | `users.is_platform_admin` is false | `PlatformAdminHandler` | `SELECT is_platform_admin FROM users WHERE email = '...';` |
| API will not start after a config change | A validated setting is wrong | `Program.cs` startup checks | The exception text names the setting (`Auth:Proxy`, `Auth:RateLimit`, `Observability`, `Email:`) |
| Database size keeps growing | Expired tokens and finished outbox rows are never deleted; audit retention off | Worker table; `AuditRetentionWorker` | `SELECT relname, n_live_tup FROM pg_stat_user_tables ORDER BY 2 DESC;` |
| Failed email never retried | `Failed` is final and the token is cleared | `EmailDeliveryService` | Ask the user to request a new link (or the admin to resend the invitation) |
| Manual `DELETE` or `UPDATE` on `audit_entries` fails with "append-only" | Trigger working as designed | `20261031194633_AddAuditLog` | Use the retention job, not manual SQL |

## Highest-risk areas in this part

1. **Proxy trust (`Auth:Proxy`, `AuthService:Proxy`).** A wrong range either collapses every visitor into one rate-limit bucket and one audit address, or lets a caller choose their address. Review the trusted network (the whole Docker subnet in the shipped stack) and `ForwardLimit`.
2. **Key-encryption secret (`Auth:Tokens:KeyEncryptionKey`).** One secret protects the signing keys and webhook secrets; losing or changing it is unrecoverable and rotation is not built. Review backup of the secret apart from database backups.
3. **Fail-closed rate limiting tied to the database.** A missed migration or a database fault turns login, reset, resend, service token and token exchange into `503`. Review the deployment order (migrate before API) and the readiness probe.
4. **Email delivery silent failure.** `Failed` rows are final, and bounces are invisible. Review alerts on the failed and oldest-pending gauges.
5. **Unbounded table growth.** No cleanup for tokens and finished outbox rows; audit retention can be switched off. Review a retention policy before the tables reach production volume.
6. **Audit append-only protection relies on triggers the application user can drop.** Review database roles (separate retention role, `INSERT`/`SELECT` only) if the log is a compliance record.

## Unknowns and assumptions to confirm

- **Assumption (confirm):** `source_ip` and the rate-limit key use `RemoteIpAddress.ToString()` with no IPv4-mapped normalisation (Audit log, Rate limiting).
- **Unknown:** whether `AuditActorType.ApiKey` is meant to be recorded for API-key calls later (no `AuditActor` factory exists).
- **Assumption (confirm):** the 50-message batch and 2-second polling interval of the email dispatcher suit expected volume (Email delivery).
- **Assumption (confirm):** a malformed GUID or date in a paging parameter is answered `400` by ASP.NET Core's binding; no test I read covers it (Paging).
- **Unknown:** whether the audit and webhook lists should move onto `PageQuery` so that `before` without `beforeId` is rejected like the other lists.
- **Assumption (confirm):** no tenant reaches the web console's read cap of 10 000 rows (50 pages of 200).
- **Assumption (confirm):** a `403` from an authorization policy returns the framework's default empty body, with no `code`; no `OnForbidden` handler is configured.
- **Unknown:** the intended cleanup policy for expired refresh, verification, reset and invitation rows and for finished email and webhook outbox rows.
- **Unknown:** how a deployment with more than one proxy hop in front of the apps should set `ForwardLimit` (the shipped stack has one).
- **Unknown:** whether a uniform `forbidden` error code is planned for authorization failures.
