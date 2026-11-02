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
src/AuthService.Authorization/     The package a product installs to enforce Auth Service tokens
src/AuthService.Web/               The Blazor web console (setup only so far)
Dockerfile, deploy/                API and migration images, and an example docker-compose
tests/AuthService.IntegrationTests/ Real-database integration tests (Testcontainers)
tests/AuthService.Web.Tests/       Tests of the web console (no database needed)
```

Deploying it (images, settings, first run, releases): [docs/deployment.md](docs/deployment.md).

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

## The web console (`src/AuthService.Web`)

A Blazor Web App ([decision 0002](docs/decisions/0002-blazor-web-app-for-ui.md)) that is one more client of the API. This is its
**setup only**: the finished screens follow the Figma design and the Jira Web UI epic (AUTH-195).

```bash
dotnet run --project src/AuthService.Web      # http://localhost:5090, expects the API at http://localhost:5080
```

What is in place:

- **The API address** is `AuthService:Api:BaseUrl` (`AuthService__Api__BaseUrl`), with `TimeoutSeconds` (default 15). It is required:
  a missing or malformed address stops the app starting, naming the setting. `appsettings.Development.json` points at the local API.
- **`AuthApiClient`** is the one way pages call the API. It turns every API error into an `ApiProblem` (status, stable `code`,
  per-field messages, `Retry-After`) instead of throwing, reports an unreachable API or a timeout as `api_unreachable`, never follows
  redirects, and never lets exception text or a proxy's error page reach a person. It holds no token itself.
- **Three page shells**: `AuthLayout` (signed-out pages: brand panel on desktop, a centred card on tablet, the form full width on
  mobile), `TenantLayout` (tenant console) and `PlatformLayout` (platform admin console, labelled so an operator knows where they are).
  The tenant and platform shells are still empty. Each has a skip link and a `main` landmark.
- **The design system** (`wwwroot/app.css`): the Signal Indigo tokens in light and dark (system preference, or `data-theme` on `<html>`),
  buttons, fields, alerts and the public shell. Shared components are in `Components/Shared` (`AlertBanner`, `TextField`,
  `PasswordField`, `Icon`). `wwwroot/app.js` only adds a show/hide password button, a busy button on submit and the rate-limit
  countdown; every page works without it. Inter is named first in the font stack but not loaded from a font host, so no page calls a third party.
- **`/login`** (UI01_T3), a server-rendered form with one wording for each answer of `POST /api/auth/login`: wrong email or password,
  email not verified, account disabled, rate limited (with a live countdown from `Retry-After`) and API unreachable. A successful
  sign-in starts the session below and goes back to the page the person wanted (`?returnUrl=`, a path on this site only).
- **`/signup`** (UI01_T2): full name, organisation name (the API creates the organisation at signup and it cannot be renamed), work
  email and password, with the password rule (8 to 128 characters, the API's own) ticking over as the person types. Success shows
  "Check your email" with a resend button that waits 60 seconds. An email that already has an account gets **the same screen**, so the
  page never says whether an address is registered; field reasons from the API, a rate limit (live countdown) and an unreachable API
  each have their own message.
- **`/verify-email`** (UI01_T4), the page the verification email opens. The link's token is held only in a form field: `app.js` takes it
  out of the address bar and the history entry, then sends the form (a POST, so an email scanner that only fetches the page cannot
  use the link up; without scripts the person presses the button). The result is shown on a clean address: verified (continue to
  sign in), or "this link can't be used" with an email box to get a new one (same neutral confirmation for any address, a resend
  button that waits 60 seconds, a rate-limit message with the wait). An unreachable API keeps the link on the page for another try.
  Pages send `Referrer-Policy: no-referrer` and are never cached, and the token is never logged.
- **`/forgot-password` and `/reset-password`** (UI01_T5). Forgot: one email box, and the same "check your email" screen whatever the
  API answers (so it never says whether an account exists), a 60-second resend button, a rate-limit wait, a calm message when the API is
  down. Reset: the emailed link's token is held only in the form and taken out of the address bar and history as the page loads (the
  form posts to a clean `/reset-password`); new password with its rule and a confirm box, the API's reasons beside the field (the link
  stays usable, because the API checks the password before it touches the link), then "Your password is changed" and on to sign in,
  or "This link can't be used" with a way to ask for a new one. There is no separate "checking your link" screen here: the API has
  no way to check a reset link without using it, so the form shows at once and the link is only used when the password is sent.
- **`/accept-invitation`** (UI01_T6). A new person chooses a password (with the rule and a confirm box) and joins; a person who already
  has an account accepts while signed in, as themselves, with no password. The API cannot say whether the invited email is new, so the
  page asks anonymous visitors for a password and, if the API answers "this email has an account", sends them to sign in and back. The
  link's token is held only in the form (taken out of the address bar and history by `app.js`) and, while the person signs in, in an
  encrypted, HttpOnly, one-hour cookie (`invitation_pending`), so it never travels in the login address; the cookie is cleared as soon
  as the invitation is used or can never work. Signed in as the wrong person: it says so and offers "Sign out and switch account",
  which returns to this page after the next sign-in. Expired, used or withdrawn invitations, rate limits and an unreachable API each
  have their own message. Success leads to `/` until the organisation picker exists.
- **Secrets on the pages** (UI01_T7): the three email-link pages (`/verify-email`, `/reset-password`, `/accept-invitation`) keep the token only
  in one hidden field, post to a clean address, have `app.js` remove it from the address bar and history, send
  `Referrer-Policy: no-referrer` and `Cache-Control: no-store`, and never log it. A password field never renders a value, so a form that is
  shown again (a validation error, a rate limit) cannot send a password back into the page, and each password field says whether it
  holds an old (`current-password`) or a new (`new-password`) password. `LinkTokenSecurityTests` covers each page.
- **`/choose-organisation`** (UI05_T1), the page everyone lands on after signing in (`/` sends signed-in people there). It reads the
  person's organisations from `GET /api/auth/users/me` as them (their access token is renewed first when needed) and lists them by name:
  Active ones open the product picker (`/choose-product?tenantId=...`, the next ticket); Invited and Disabled ones are shown with why
  they can't be opened and are not links. Someone in exactly one active organisation is sent straight on (`?change=1` shows the list
  anyway); someone in none sees an empty state with the create form open; "Create another organisation" is a plain `<details>` form
  (`POST /api/auth/tenants`). A list that cannot be loaded shows a calm error with a short reference; a session the API no longer
  accepts goes back to sign in. The shell is `PickerLayout` (logo, an account menu with sign-out, one centred column). The API sends
  no role per organisation, so rows show status only.
- **The tenant console** (`/console/{tenantId}/...`, UI-02), starting with **Members** (UI02_T1). `ConsoleSession` opens an organisation for the signed-in
  person: they must be an active member (`users/me`), then their sign-in is traded for a token for the Auth Service's own product
  (`POST /api/auth/token/exchange`, application `00000000-0000-7000-8000-00000000a07a`), kept on the server in `ConsoleTokenCache` until a minute
  before it expires and never shown or logged. What the token allows (`permissions`, `key:Level`) decides what the shell and pages show:
  navigation lists only built pages the person can read; a change they may not make is a disabled button with a tooltip naming the
  permission, and the API still enforces it. `TenantLayout` is the shell (sidebar, a drawer on small screens, header with the
  organisation and the person's menu). **Members** lists name, status, roles and products (cards on small screens), with search and a status
  filter, and enables or disables people: disabling asks first and says what it does; the change is a plain POST (`ConsoleEndpoints`) that
  redirects back with a notice (with Undo) or an error code the page explains in plain words (last Owner, only an Owner can disable an
  Owner, not accepted yet, no free seat in named products, no permission, API down). Active organisations in the picker have a Manage
  link.
- **Invitations** (`/console/{tenantId}/invitations`, UI02_T2 and T3). The pending list shows email, status (expired ones first and flagged), roles, products
  (named when the person may read subscriptions, counted otherwise), who invited, when it was sent and when it expires. **Resend** (needs
  `members.invite:Full`) shows the API's one-minute wait as a live countdown worked out from when it was last sent; resending too soon, an
  invitation already accepted and a missing invitation are each explained. **Invite people** opens a dialog (`?invite=1`, plain HTML): one or
  many addresses (a line or comma each, up to 20), 1 to 10 roles from the role list (Owner only offered to an Owner), and optional product access
  with seats shown (full or unsubscribed products can't be chosen). Each address is invited in turn; a refusal (already a member, Owner
  role restricted, product not subscribed, no free seat in named products, role gone, API down) is explained and only the addresses that still
  need sending stay in the box. Inviting needs `members.invite:Full` and `roles.read`.
- **Roles** (`/console/{tenantId}/roles`, UI02_T4). Lists the organisation's roles (the built-in Owner first and locked), with how many people hold each and a
  one-line permission summary. **Delete** asks first; a role nobody holds says what deleting does, and a role someone still holds says how many people hold it
  and what to do next (give them a different role, with a link to Members) instead of offering the button. A refusal from the API is explained in the
  same terms. Create and Edit link to the role editor (the next ticket); without `roles.manage:Full` those buttons are disabled with the permission named.
- **Role editor** (`/console/{tenantId}/roles/new` and `/roles/{id}`, UI02_T5). A name, then one section per product the organisation uses (the Auth Service itself first) with a
  level for every permission: no access, view only, view and create, full access. The sections come from `GET /api/auth/tenants/me/permissions`, so every
  choice offered is one the API accepts. Saving sends only the permissions above no access (`POST` to create, `PATCH` to replace the set). A name already
  used, the API's own reasons and an unreachable API are explained and the choices are kept; permissions of a product that is no longer active are flagged
  (saving removes them); the built-in Owner role can't be edited. `app.js` warns before leaving a form marked `data-unsaved-guard` once it has been changed.
- **Assigning roles** (`/console/{tenantId}/members/{id}/roles`, UI02_T6), reached from a Roles action on each member. The roles the person holds are ticked; 1 to 10 can be
  chosen and the choice replaces what they have (`PUT`). The Owner role is only for an Owner to give or remove: for anyone else it is disabled, and if the member already
  holds it, it is sent along unchanged so it is not removed by accident. Choosing none is refused with a pointer to disabling the member; the API's refusals (a member needs
  a role, only an Owner can do that, the last Owner can't step down, something changed) are explained in plain words with the choices kept. The page says a change reaches
  the person within about 15 minutes. Needs `roles.assign:Full` plus read access to members and roles.
- **Product access** (`/console/{tenantId}/access`, UI02_T7). One card per subscribed product: its status, a seat meter ("3 of 5 seats used", **Almost full** from 80%, **No seats left** at the
  limit, "no seat limit" when unlimited; the numbers come from the subscriptions list), who has access, and a way to give it. **Give access** offers only people who don't have it and
  aren't disabled (a disabled person keeps their access row but uses no seat); a full product disables it with the reason; a suspended or cancelled product can't be given to anyone.
  **Remove access** asks first (seat freed; takes effect at their next token, within about 15 minutes). Both are plain POSTs that redirect back with a notice or an error code
  explained in plain words (no seat, disabled member, product not active, member gone, no permission, API down). Needs `access.manage:Full` to change, and read access to
  subscriptions and members to see.
- **Permission-aware console** (UI02_T8). The console token's permissions decide what each page offers: an admin who may only read sees the lists but every write is a disabled button with a
  tooltip naming the permission, or absent; a forced `?disable=`, `?delete=`, `?revoke=` or `?invite=1` link shows no confirmation or dialog; the editor pages say which permission is
  needed; a list the API refuses with 403 reads as "you can't see this", not a broken page; and a forced write the API refuses comes back as "You don't have permission to do that"
  naming the permission, never a raw code. The server is still the authority. `PermissionAwareConsoleTests` runs these across every console page.
- **API keys** (`/console/{tenantId}/api-keys`, UI03_T1). Lists keys masked, with product, permission count, status (active, expiring, expired, revoked) and dates. **Create API key**
  opens a dialog: a name, which product the key is for (a plain form that reloads the permission list), and a level for each permission, limited to what the creator holds
  (for the Auth Service itself; the API checks other products). The key's raw value comes back once: `OneTimeSecrets` keeps it on the server for the creating session for two
  minutes and the redirect carries only a random reference, so it is never in an address, never logged, and a reload or another person finds nothing. It is shown in a dialog
  with the warning "You won't see this again", a read-only field, a Copy button that appears once scripts load, and one button, "I've saved it".
  **Rotate and revoke** (UI03_T2): an active key's row has Rotate and Revoke, a key being rotated out (Expiring) only Revoke, and a finished key nothing. Rotate asks how long the old
  key keeps working (not at all, 1 hour, 24 hours suggested, 3, 7 or 30 days), then shows the new value in the same once-only dialog, with a note saying when the old key stops.
  Revoke is a confirmation naming the key and what happens; both are plain posts, anti-forgery checked, and the dialogs only open for someone who may manage keys. The API's reasons
  (not active, not found, no permission, down) are explained in plain words.
- **The signed-in session** (UI01_T1, [decision 0006](docs/decisions/0006-signed-in-session-kept-on-the-server.md)): the browser gets one
  `HttpOnly` cookie with a session id and never a token; the tokens stay on the server in an `ISessionStore` (in memory by default).
  The access token is renewed a minute before it runs out, one request at a time per session, because each refresh token works once.
  A session the API will not renew sends the person to `/login?reason=expired`; an unreachable API does not sign anyone out.
  `POST /logout` (form with an anti-forgery token) ends it here and at the API. `/account` is the first page that needs sign-in
  (`[Authorize]`). **One copy of the web app only** until a shared store and shared data-protection keys exist. Settings:
  `AuthService:Session:CookieName`, `RememberedDays` (30), `DefaultHours` (12), `RefreshBeforeSeconds` (60). Nothing logs a password,
  token or session id.
- **Security headers** on every response: `Referrer-Policy: no-referrer`, `X-Content-Type-Options`, `X-Frame-Options: DENY`, a
  `frame-ancestors`/`form-action` policy and `Permissions-Policy`; HTML pages are `Cache-Control: no-store`. A script-source policy is
  decided with the finished design, because Blazor's scripts need a tested nonce policy.
- **`GET /healthz`** answers `ok` while the app runs, and says nothing about the API.
- **Not yet:** the other auth screens (signup, verify email, password reset, accept invitation), the consoles and the picker, a shared
  session store for more than one copy, and a container image.

`tests/AuthService.Web.Tests` tests it with no database and no Docker (`dotnet test tests/AuthService.Web.Tests`).

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
| `Auth:Proxy:TrustedProxies` / `TrustedNetworks` / `ForwardLimit` | The reverse proxies in front of the service: addresses (`10.0.0.5`) and/or ranges (`10.0.0.0/8`), as arrays (`Auth__Proxy__TrustedProxies__0`). Only from these is `X-Forwarded-For` believed, and then the client address replaces the proxy's in per-IP rate limits and audit entries; from any other address the header is ignored. `ForwardLimit` (default 1, 1 to 10) is how many hops, counted from the right, to believe. **Leave unset if nothing sits in front, and set it whenever something does**: a typo stops the service from starting. |
| `Auth:Tokens:AccessTokenMinutes` | Identity access token lifetime. Default 15. |
| `Auth:Tokens:RefreshTokenDays` | Refresh token lifetime. Default 30. |
| `Auth:TokenExchange:UserLimit` / `UserWindowMinutes` | How many token exchanges one signed-in person may make per window before they get 429 with `Retry-After`. Default 30 per 1 minute. |
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
| `Auth:Invitations:TokenDays` / `ResendCooldownSeconds` | How long an invitation link works (default 7 days), and how long after a link is emailed it cannot be emailed again (default 60 seconds). |
| `Auth:ApiKeys:RotationGraceHours` | How long an API key keeps working after it is rotated. Default 24. |
| `Auth:Webhooks:RetryWindowHours` / `RetryDelaySeconds` / `TimeoutSeconds` | How long a webhook is retried (default 24 hours), the delays between tries (default 1 min, 5 min, 30 min, 2 h, 6 h, 12 h) and how long to wait for a product to answer (10 s). |
| `Auth:Webhooks:AllowPrivateAddresses` | Allows plain `http` and private callback addresses. Development and tests only; default off. |
| `Auth:Webhooks:DispatcherEnabled` | Turns the webhook worker off, so events wait. Default on. |
| `Auth:Applications:SecretGraceHours` | How long a product's old client secret keeps working after a rotation. Default 24. |
| `Auth:Applications:TokenIpLimit` / `TokenIpWindowMinutes` | How many service-token requests one IP may make per window, across all products, before getting 429. Default 60 per minute. |
| `Auth:LoginProtection:IpLimit` / `IpWindowMinutes` | How many login requests one IP may make per window, across all accounts, before getting 429. Default 20 per minute. |
| `Auth:EmailVerification:TokenHours` | How long a verification link works. Default 24. |
| `Auth:EmailVerification:ResendCooldownSeconds` | An account sent a link less than this long ago is not sent another. Default 60. |
| `Auth:EmailVerification:ResendIpLimit` / `ResendIpWindowMinutes` | How many resend requests one IP may make per window before getting 429. Default 10 per 10 minutes. |
| `Auth:PasswordReset:TokenMinutes` | How long a password reset link works. Default 60. |
| `Auth:PasswordReset:RequestCooldownSeconds` | An account sent a reset link less than this long ago is not sent another. Default 60. |
| `Auth:PasswordReset:IpLimit` / `IpWindowMinutes` | How many forgot or reset requests one IP may make per window before getting 429. Default 10 per 10 minutes. |

**Local secrets.** In Development, keep secrets such as the Brevo API key out of the repository with .NET user secrets (read in
Development only, so the tests never see them):

```bash
dotnet user-secrets set "Email:BrevoApiKey" "<your key>" --project src/AuthService.Api
dotnet user-secrets set "Email:FromAddress" "<a sender verified in Brevo>" --project src/AuthService.Api
```

Then run with `ASPNETCORE_ENVIRONMENT=Development` and `Email__Provider=Brevo`. Do not run `dotnet user-secrets list` where others can
see your screen: it prints the values.

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
  with `Retry-After`. Behind a reverse proxy, list the proxy in `Auth:Proxy` (see Configuration) so this is the client's address and not the
  proxy's.

The lockout is logged by user id, never by email. The audit log (AUD-01) will record it too once it exists, and password
reset (TEN-10) will clear the count.

### Audit log

Security events are recorded in the `audit_entries` table so who did what, when and in which tenant can always be traced. Each
entry has **who** (user, product, API key, the system, or anonymous), **what** (a stable code such as `auth.login.succeeded`),
**when**, the **tenant** if there is one, the caller's **IP address**, and an optional short code of detail (`wrong_password`).

What is recorded: signup, tenant creation, login success and failure (with the reason: wrong password, unknown account, locked,
disabled, email not verified), lockouts, email verification, completed password resets, refresh-token reuse, signing-key creation and
rotation, platform-admin actions (registering a product, its catalog, activation and secret rotation, reading the log), subscriptions,
suspensions and seat-limit changes, role creation, changes and deletion, member role, access and status changes, invitations sent, accepted and resent, and
reads of the tenant's own log. API-key events are recorded when those stories land. The codes live in `AuditActions`.

- **Written with the action, not after it.** An entry is saved in the same transaction as the action it records, so it exists if
  and only if the action happened. A signup that rolls back leaves no entry.
- **No secrets, by construction.** The only free text is a short code of up to 128 characters (lowercase letters, digits, dots,
  underscores and hyphens, such as a membership id and a product key), so a
  password, token or address cannot be recorded by mistake. A failed login for an unregistered email records nothing about the
  email that was tried.
- **Append-only, enforced by the database.** Triggers refuse every `UPDATE`, every `TRUNCATE`, and every `DELETE` except the
  retention job's, which switches on a setting that lasts only for its own transaction. No endpoint edits or deletes entries.
  The application's own database user can still change the triggers, so for stronger protection give it `INSERT` and `SELECT`
  only on this table and run the retention job as a separate role.
- **Kept for a year.** A daily job deletes entries older than `Auth:Audit:RetentionDays` (365).
- **The IP is the caller's**: the connection's address, or, behind a proxy listed in `Auth:Proxy`, the client address it forwards; if the
  proxy is not listed, the proxy's own address.

A platform admin reads the whole log with `GET /api/auth/admin/audit` (see "Platform admins" below).

**A tenant admin reads their own tenant's entries** with `GET /api/auth/tenants/me/audit`, called with a **tenant-admin token** (see
"Working on a tenant") that holds `audit.read` at `ReadOnly` (the Owner does).

- The tenant is the token's `tid`; a `tenantId` in the query string is ignored, and entries that belong to no tenant (such as failed
  logins) are never shown. Another tenant's entries cannot be reached.
- It pages exactly like the platform read: newest first, `limit` 1 to 200 (default 50), filters `action` and `actorId`, and the
  returned `next` values passed back as `before` and `beforeId`.
- Reading the log is itself recorded (`auth.tenant.audit_read`), after the query, so a read never lists itself.

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

**Rate limit.** Each exchange reads the person's membership, subscription and roles from the database, so exchange is limited **per
signed-in person**: 30 a minute by default (`Auth:TokenExchange:UserLimit`), after which the answer is **429** `rate_limited` with
`Retry-After` and no token. Refused exchanges count too, so guessing tenant or product ids is limited as well, and an exchange for
the Auth Service's own product shares the same budget. A person is not penalised for sharing an address with others, and a caller
with no valid token is limited per IP address instead. A token lasts 15 minutes, so a real person needs a handful an hour.

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

#### The authorization package (what a product installs)

A product does not write its own token checks. It installs the **`AuthService.Authorization`** package
(`src/AuthService.Authorization`, packed as a NuGet package; its own [README](src/AuthService.Authorization/README.md) has the
details), which checks a product token **locally**, against the keys in the JWKS, with no call to the Auth Service per request:

```csharp
builder.Services.AddAuthServiceAuthorization(options =>
{
    options.Issuer = "AuthService";
    options.Audience = "whatsapp-saas-api";   // the product's own audience, as registered
    options.JwksUrl = new Uri("https://auth.example.com/.well-known/jwks.json");
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/campaigns", (ClaimsPrincipal user) => List(user.RequireTenantId())).RequirePermission("campaign.read", AccessLevel.ReadOnly);
```

- **Accepted:** an ES256 signature from a published key, the Auth Service's issuer, the product's **own audience**, and an unexpired
  token (30 seconds of clock skew). Everything else is a plain **401** `application/problem+json` (`code: unauthorized`) that says
  nothing about why: another product's token, an identity or service token, a tampered, expired, unsigned (`alg: none`), HMAC-signed or
  malformed token, or no token.
- **Permissions** are checked by level, never by role name: `RequirePermission("campaign.create", AccessLevel.CreateRead)` is met by
  `CreateRead` or `Full` on that permission, and anything else is **403** `forbidden`. **The tenant** is `User.RequireTenantId()`, the
  token's `tid`, and must be taken from nothing else.
- **Keys** are fetched once and refreshed every `KeyCacheMinutes` (10) and when a token names a key the product does not have (at
  most every `MinimumRefreshSeconds`, 30), which is how a rotation is picked up with no restart. If a refresh fails the last good keys
  are kept; if the first fetch fails every token is refused (401) and the Auth Service is retried at that interval, not per request.
- **A forgotten policy fails a test.** `EndpointPolicyAudit.FindEndpointsWithoutPermissionPolicyAsync` lists any endpoint of the
  product that is not covered by a permission policy, for a test that asserts the list is empty (health checks are passed as exempt).
- The JWKS URL must be https (`AllowInsecureJwksUrl` is for local development), and a missing audience or URL stops the product at
  startup rather than at the first request.

The claim contract (`sub`, `tid`, `aud`, `permissions` as `key:Level`) and the package's public API are versioned together under
semantic versioning (currently 1.1.0): a breaking change to either is a new major version. The package judges token lifetimes on a
`TimeProvider` the product registers (the system clock otherwise), so a test can move time. The tests run a small product API in the
test process against the running Auth Service (`tests/AuthService.IntegrationTests/Tok03`).

#### Isolation tests (always run)

`tests/AuthService.IntegrationTests/Tok04` runs on every build and fails it if tenants or products stop being separate:

- **Tenant A's token never reaches tenant B's data.** Every tenant-scoped read is checked for B's ids, names and people, every attempt
  to address B's role, member, seat or key is a `404` that leaves B's data unchanged, and a request that names another tenant in its
  query, header or body is served from the token's tenant only.
- **One product's token is rejected by another** (`aud`), and a tenant-admin token does not work at a product or the reverse.
- **A person in two tenants** cannot use the token for one to act in the other, and being Owner of A gives nothing in B.
- **A role downgrade** is in the next token the person gets; the token issued before it works until its 15 minutes run out (the
  documented bound) and is refused after that. Removing a seat stops new tokens at once.
- **An endpoint inventory**: every endpoint is either on the short reviewed list of anonymous ones or carries an explicit rule, every
  tenant-scoped one requires a tenant-admin token and takes its tenant from it (never a route value), and everything under
  `/api/auth/admin` needs a platform admin. A new endpoint added without a decision fails this test.

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
  subscription: if the subscription fails, no event exists. Its id is the event id the product deduplicates on. Delivery is
  described under "Webhooks to products" below.
- Subscribing is audit-logged with the product as the actor and the tenant it concerns.

#### Webhooks to products

When a tenant is subscribed (`ApplicationSubscribed`), or suspended or cancelled (`ApplicationUnsubscribed`, with the new `status`),
the service tells the product. Events are written to the `webhook_outbox` table in the same transaction as the change, so an event
exists if and only if the change does, and a background worker delivers them.

**Registering where to send them.** A product calls, with its **service token**:

- `PUT /api/auth/applications/me/webhook` with `{"url": "https://hooks.example.com/auth", "rotateSecret": false}`. The URL must be
  **https** and a **public** address: `http`, `localhost`, private, link-local and `.internal` hosts, credentials and fragments are
  **400**, and the address is checked again when it is called, so a name cannot later be pointed inside a network
  (`Auth:Webhooks:AllowPrivateAddresses` relaxes this for development only). The first call creates the product's **signing
  secret** (`whsec_...`) and returns it **once**, with `Cache-Control: no-store`; later calls return none unless
  `"rotateSecret": true`, which replaces the secret at once (later events are signed with the new one). It is stored encrypted, never
  logged, and never recorded in the audit log.
- `GET /api/auth/applications/me/webhook` returns the URL and whether a secret exists.

**What the product receives.** A `POST` with the JSON body (it holds `eventId`, `eventType`, `occurredAt`, `tenantId`,
`applicationId` and the event's details) and these headers:

| Header | Value |
|---|---|
| `X-Auth-Event-Id` | The event id, the same on every retry. **Use it to drop duplicates**: delivery is at-least-once. |
| `X-Auth-Event-Type` | `ApplicationSubscribed` or `ApplicationUnsubscribed`. |
| `X-Auth-Timestamp` | When this attempt was made, in Unix seconds. Reject one that is too old to stop replays. |
| `X-Auth-Signature` | `v1=` and the lowercase hex HMAC-SHA256, with the signing secret, of `{timestamp}.{body}`. |

Any **2xx** answer is success. Anything else (another status, a redirect, which is never followed, a timeout or a connection error)
is a failure.

**Retries.** A failed delivery is retried after 1 minute, 5 minutes, 30 minutes, 2 hours, 6 hours and then every 12 hours, until the
event's **24-hour deadline** (`Auth:Webhooks:RetryWindowHours`). One last attempt is made at the deadline and then the event is
marked **Failed**. An event whose product has registered no URL waits, and fails the same way if none arrives within the 24 hours.
Several instances can run at once without delivering one event twice at the same time. Logs name only ids and outcomes, never a URL.

**Platform admins** see and resend them:

- `GET /api/auth/admin/webhooks` lists events newest first with `status` (Pending, Delivered or Failed), `attempts`, `lastError`
  (such as `HTTP 503`), times and the product. Filters `status` and `applicationId`; `limit` 1 to 200 and the returned `next` values
  as `before` and `beforeId` for paging.
- `POST /api/auth/admin/webhooks/{id}/resend` puts a **Failed** event back in the queue with a fresh 24 hours (anything else is
  **409** `webhook_not_failed`, an unknown id **404**). It is audit-logged (`auth.webhook.resent`).

### API keys

A tenant's own backend can call a product without a person logging in, using an **API key**. Keys are managed by a tenant admin with
a **tenant-admin token** (see "Working on a tenant") and checked by the product through the Auth Service.

**Creating a key.** `POST /api/auth/apikeys` (needs `apikeys.manage` at `Full`, which the Owner has):

```json
{ "name": "Billing backend", "applicationId": "...", "permissions": [ { "permission": "message.send", "level": "Full" } ] }
```

- The reply (**201**, `Cache-Control: no-store`) holds the **raw key**, such as `ak_3f9c...e1.Xk2...`, **once** and nowhere else.
  It is never returned again, logged or audited. Only a **salted SHA-256 hash** is stored (every key has its own salt), and the key is
  looked up by its public id (the 24 hex characters after `ak_`) and checked in constant time.
- **Scope** is chosen at creation and never grows. `applicationId` names the **one product** the key may be used with, and the tenant
  must be actively subscribed to it; `permissions` are keys from that product's catalog, each at a `level` (`ReadOnly`,
  `CreateRead`, `Full`; left out means `Full`). A product needs at least one permission. Leave out both and you get a key that only
  identifies the tenant and grants **nothing**. The Auth Service's own product cannot be used. An unknown or unsubscribed product, an
  unknown permission, a repeat or a bad level is **400**.
- **A key can never have more than the person making it.** Each permission must be within what that person's own roles grant, at
  that level or lower, otherwise **403** `scope_exceeds_permissions`.
- Audit-logged (`auth.apikey.created`, with the key's id).

**Seeing keys.** `GET /api/auth/apikeys` and `GET /api/auth/apikeys/{id}` (need `apikeys.read`) show name, a masked key
(`ak_3f9c1a2b...`), product, scope, `status` (`Active`, `Expiring` while being rotated out, `Expired`, `Revoked`) and times, never
the raw key. A key of another tenant, or an unknown id, is **404**, never 403.

**Revoking.** `DELETE /api/auth/apikeys/{id}` (needs `apikeys.manage`) ends a key at once (**204**; again is a no-op). Its next use is
refused. It touches no person's login: keys and sessions are separate credentials.

**Rotating.** `POST /api/auth/apikeys/{id}/rotate` (needs `apikeys.manage`; body optional `{"graceHours": 24}`, 0 to 720) returns a
**new raw key once** (**201**) with the **same scope**. The old key keeps working for the grace period (24 hours by default,
`Auth:ApiKeys:RotationGraceHours`) and then stops; `0` cuts it off at once, for a leaked key. Rotating again never extends an older
key's life. A revoked or expired key cannot be rotated (**409** `api_key_not_active`). Rotation is audit-logged (`auth.apikey.rotated`).

**Checking a key (for products).** A product sends what its client presented to
`POST /api/auth/apikeys/validate`, with its **service token**: `{"key": "ak_...", "permission": "message.send", "level": "Full"}`
(`permission` and `level` are optional; `level` defaults to `ReadOnly`).

- **200** returns `{tenantId, keyId, name, applicationId, permissions}`: whose key it is and what it may do. The product must use that
  tenant and nothing else.
- **401** `invalid_api_key` for a key that is malformed, unknown, revoked, expired, belongs to a suspended tenant, or was made for
  **another product**. It is one answer for all of them and means "no credential": a product must refuse the call, never treat it as
  an anonymous or tenant-less caller.
- **403** `outside_key_scope` when `permission` is not among the key's permissions at that level or above (a key for no product
  grants nothing), and `subscription_not_active` when the tenant's subscription to the product is no longer active.

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

**What can be granted.** `GET /api/auth/tenants/me/permissions` (a tenant-admin token holding `roles.read`) lists what a role can grant:
the `levels` (`ReadOnly`, `CreateRead`, `Full`) and, for the Auth Service itself first and then each product the tenant has an
**Active** subscription to, its `applicationId`, `key`, `name` and every permission with its `key`, `label` and `description`. It is
exactly the set a role grant is accepted for, so a role-building screen can offer only choices that will be accepted. A product
whose subscription is Suspended or Cancelled, and one the tenant never used, is not listed.

#### Inviting people

`POST /api/auth/tenants/me/invitations` (a tenant-admin token holding `members.invite` at `Full`) invites a person by email:

```json
{ "email": "new.person@example.com", "roleIds": ["..."], "applicationIds": ["..."] }
```

- **Roles are required: 1 to 10** of the caller's tenant (another tenant's role is **404**). Only an Owner can invite someone as an
  Owner (**403**). `applicationIds` is optional and gives the person access to those products straight away; an Invited person
  **takes a seat**, so a full product refuses the whole invitation (**409** `no_seat_available`) and nothing is created, and a
  product the tenant is not actively subscribed to is **400**.
- The email is trimmed and lowercased. An email that already has a membership in this tenant, in any status and whatever its
  case, is **409** `already_member`. The reply (**201**, `membershipId`, `status: Invited`, roles, `expiresAt`) is the same
  whether the email was new to the platform or already had an account, and never contains the link, so inviting reveals nothing
  about other tenants.
- A person new to the platform gets a user **with no password**, who cannot log in (the same generic 401 as bad credentials) until
  they accept. The service emails a single-use link; the token is stored only as a hash and the link works for **7 days**
  (`Auth:Invitations:TokenDays`).

`POST /api/auth/invitations/accept` with `{"token": "...", "password": "..."}` (no login needed for a new person):

- **A new person** chooses a password (8 to 128 characters) and their email counts as **verified**, since the link proved they own
  it, so they can log in at once. A missing or bad password is **400** and the link stays usable.
- **A person who already has an account** must be **logged in as the invited email** (send their identity token): not logged in is
  **401** `login_required`, a different account **403** `wrong_account`, and a password in the body is **400**, since accepting
  never sets or changes one. A mistake does not use up the link. Their memberships in other tenants are untouched.
- An unknown, expired (after 7 days), already used or withdrawn (membership disabled) link is always **410**
  `invitation_invalid`, and two simultaneous acceptances succeed only once. Accepting makes the membership **Active**.
- Inviting and accepting are audit-logged (`auth.invitation.sent` / `accepted`, with the membership id).

**Who has not accepted yet, and sending the link again.**

- `GET /api/auth/tenants/me/invitations` (`members.read` at `ReadOnly`) lists the invitations nobody has accepted, newest first: the
  `membershipId`, `email`, `roles`, `applicationIds`, who invited them (`invitedBy`), `invitedAt`, `lastSentAt`, `expiresAt` and
  `isExpired`. An accepted invitation is not listed; an expired one is, flagged, because that is the one to send again. Only the
  caller's tenant is ever listed.
- `POST /api/auth/tenants/me/invitations/{membershipId}/resend` (`members.invite` at `Full`) emails a **new** link (the old one stops
  working at once) and gives a full period again, so it also revives an expired invitation. The reply is the invitation as when
  it was sent (**200**). A person who has already accepted, or was never invited, is **409** `invitation_not_pending`; a
  membership of another tenant or an unknown id is **404**. Within 60 seconds of the last send (`Auth:Invitations:ResendCooldownSeconds`)
  it is **429** `resend_too_soon` with `Retry-After` and sends nothing, so it cannot be used to flood an inbox; simultaneous requests
  send one email. Resending is audit-logged (`auth.invitation.resent`, with the membership id).

#### Members: listing, enabling and disabling

Called with a **tenant-admin token**; both act only on the token's tenant. A membership of another tenant, or one that does not
exist, is always **404**.

- **`GET /api/auth/tenants/me/members`** (needs `members.read`, ReadOnly) lists the tenant's people by email:
  `{membershipId, email, displayName, status, roles, applications, createdAt}`, where `applications` are the products they have a
  seat in. Invited people are included.
- **`PATCH /api/auth/tenants/me/members/{membershipId}`** (needs `members.manage`, Full) with `{"status": "Disabled"}` or
  `{"status": "Active"}`. Anything else is **400**; setting the status a member already has is a no-op.
  - **Only this tenant's membership changes.** The same person's memberships elsewhere are untouched, and their roles, product
    access and history are all kept, so enabling restores them as they were. A disabled member cannot exchange a token for this
    tenant (the next exchange is refused); tokens already issued run out within 15 minutes.
  - **Disabling frees their seats** in every product at once (a Disabled member's access row stays but uses no seat).
  - **Enabling re-checks every product they have access to against its seat limit.** If any has no free seat the answer is **409**
    `no_seat_available`, naming the product(s), and the member stays Disabled. Free a seat or raise the limit, then try again.
  - An **Invited** person becomes Active by accepting the invitation, not by being enabled (**409** `membership_invited`), but an
    invitation can be withdrawn by disabling it.
  - **Owners are protected, as with roles:** only an Owner can disable an Owner (**403** `owner_role_restricted`), and the last
    active Owner cannot be disabled (**409** `last_owner`).
  - Audit-logged (`auth.membership.disabled` / `enabled`, with the membership id).

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

Behind a reverse proxy, list the proxy in `Auth:Proxy` (see Configuration) so the per-IP limit sees the client's address and not
the proxy's.

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
