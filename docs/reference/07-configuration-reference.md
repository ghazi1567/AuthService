# Configuration reference

> Part 7 of the Auth Service reference. Overview, glossary and symptom index: [README](README.md).

This is the complete list of settings for the API (`src/AuthService.Api`), the web console (`src/AuthService.Web`) and the compose stack
(`deploy/`). Every row was taken from code: the `*Options` classes, `Program.cs`, the `appsettings*.json` files, `deploy/docker-compose.yml`,
`deploy/.env.example`, the `Dockerfile`, and the test factory `tests/AuthService.IntegrationTests/Infrastructure/AuthApiFactory.cs`.
How each feature uses its settings is explained in the other parts (for audit, email, rate limits, proxy, health: [05 Platform and cross-cutting
behaviour](05-platform-and-cross-cutting.md)).

## Contents

1. [How settings reach the apps](#how-settings-reach-the-apps)
2. [Connection and secrets](#connection-and-secrets)
3. [Tokens and keys](#tokens-and-keys)
4. [Login and passwords](#login-and-passwords)
5. [Email](#email)
6. [Webhooks](#webhooks)
7. [Rate limits](#rate-limits)
8. [Proxy and metrics](#proxy-and-metrics)
9. [Audit](#audit)
10. [Web console](#web-console)
11. [Deployment variables for the compose stack](#deployment-variables-for-the-compose-stack)
12. [What the tests override](#what-the-tests-override)
13. [Settings that are not settings](#settings-that-are-not-settings)
14. [Unknowns and assumptions to confirm](#unknowns-and-assumptions-to-confirm)

---

## How settings reach the apps

Both apps use the standard ASP.NET Core configuration (`WebApplication.CreateBuilder`). Later sources override earlier ones:

1. `appsettings.json` (committed; holds only logging and `AllowedHosts`, see below).
2. `appsettings.{Environment}.json`. Only `appsettings.Development.json` exists, in both projects.
3. .NET user secrets, in Development only (the README uses them for the Brevo key).
4. **Environment variables.** This is how production is configured. A colon in a setting name becomes a **double underscore**:
   `Auth:LoginProtection:MaxFailedAttempts` is `Auth__LoginProtection__MaxFailedAttempts`.
5. Command-line arguments (standard host behaviour; not used by the repository).

Rules that matter:

- **Arrays** are numbered: `Auth__Proxy__TrustedNetworks__0`, `__1`, and so on. Configuration binding *appends* to an array's existing
  values, which is why `Email:RetryDelaySeconds` and `Auth:Webhooks:RetryDelaySeconds` default to null and keep their real default in code.
- **Blank list entries are ignored** for `Auth:Proxy`, `AuthService:Proxy` and `Observability:AllowedNetworks`, because hosting platforms often
  pass an empty variable for an unused setting. A blank value for other settings is **not** ignored: for example an empty
  `Auth__RateLimit__Store` stops the API from starting.
- **The environment name** is `ASPNETCORE_ENVIRONMENT`. The images set `Production`; `launchSettings.json` sets `Development` for local runs; the
  integration tests use `Testing`. Three behaviours depend on it: the key-encryption key may be omitted in `Development` and `Testing`; the email
  provider defaults to `File` in Development and `InMemory` in `Testing`; and the web console's cookies are `Secure` except in Development.
- **Production, with the compose stack:** you edit `deploy/.env` (copied from `deploy/.env.example`; `.env` is git-ignored). `deploy/docker-compose.yml`
  turns those values into environment variables for each container (table "Deployment variables" below). Any other setting in this
  document can be added to the `environment:` section of the `api` or `web` service the same way, or the compose file replaced.
- **Local development:** the API reads `src/AuthService.Api/appsettings.Development.json` (database `localhost:5432`, `Auth:RateLimit:Store` =
  `Memory`) and listens on `http://localhost:5080`; the web console reads `src/AuthService.Web/appsettings.Development.json`
  (`AuthService:Api:BaseUrl` = `http://localhost:5080`) and listens on `http://localhost:5090` (`Properties/launchSettings.json`).
- Containers listen on port 8080 (the .NET default for containers, per the `Dockerfile` comments). Only the proxy publishes ports.
- **Startup checks.** The API stops at startup, with an exception that names the setting, for: a missing connection string, a missing or
  malformed key-encryption key (outside Development and Testing), a missing or unsupported email provider, an incomplete Brevo setup, a bad
  `Auth:Proxy`, `Observability:AllowedNetworks` or `Auth:RateLimit` value. The web console stops for a missing or malformed
  `AuthService:Api:BaseUrl`, a bad `AuthService:Proxy`, or a bad `AuthService:Session` value. Most other numeric settings are **not validated**, so
  a nonsense value (zero, negative) is accepted silently.

The committed `appsettings.json` files (API and web) contain only:

```json
{ "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } }, "AllowedHosts": "*" }
```

`AllowedHosts` is `*` (any `Host` header is accepted); host filtering is left to the reverse proxy.

---

## Connection and secrets

Secrets in this document: `Auth:Tokens:KeyEncryptionKey`, `Email:BrevoApiKey`, the database password inside `ConnectionStrings:AuthDb`
(and `POSTGRES_PASSWORD` in the stack). Never put them in a committed file, a chat or a log.

| Setting (colon / environment) | Default | Required (and where it fails) | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `ConnectionStrings:AuthDb` / `ConnectionStrings__AuthDb` | none in production. Development file: `Host=localhost;Port=5432;Database=auth;Username=postgres;Password=postgres` | **Required.** Missing: `Connection string 'AuthDb' is not configured.` thrown when the database context is first created (the API fails during startup, at the signing-key load) | The PostgreSQL 17 database that holds all state. The API never migrates it; the migration job must have run | Contains the password. Compose builds it from `POSTGRES_PASSWORD` and appends `GSS Encryption Mode=Disable` to silence a harmless missing-library message. Pointing a new version at an old schema makes `/health/ready` fail |
| `Auth:Tokens:KeyEncryptionKey` / `Auth__Tokens__KeyEncryptionKey` | none | **Required outside Development and Testing.** Missing: `Auth:Tokens:KeyEncryptionKey is not configured.` at startup. Not base64: `... is not valid base64.` Not 32 bytes: `... must be 32 bytes (base64 of 32 random bytes).` | AES-256-GCM key (`openssl rand -base64 32`) that encrypts the private signing keys in `signing_keys`, and the webhook signing secrets (`KeyEncryption`). In Development and Testing, when unset, a fixed publicly known key is used | **Highest-risk secret.** Changing or losing it makes every stored signing key (and webhook secret) unreadable; re-encryption is not built. Back it up apart from database backups. A leaked key plus a leaked database lets an attacker forge tokens |
| `AUTH_DB_DESIGN_CONNECTION` (environment only) | `Host=localhost;Port=5432;Database=auth_design;Username=postgres;Password=postgres` (fallback in code) | No | Used only by the `dotnet ef` design-time factory (`AuthDbContextDesignTimeFactory`). `migrations add` never connects | Development tool only; never set in production |
| `--connection "<string>"` (argument of the `authservice-migrate` image) | none | **Required** for the migration job | The database the migration bundle (`efbundle`) updates | Contains the password; in compose it is built from `POSTGRES_PASSWORD` |

---

## Tokens and keys

Bound to `Auth:Tokens` (`IdentityTokenOptions`), plus two grace periods that follow the same pattern. None of these is validated.

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `Auth:Tokens:Issuer` / `Auth__Tokens__Issuer` | `AuthService` | No (compose: `AUTH_ISSUER`, default `AuthService`) | The `iss` claim of every token, and the issuer the API validates. Products must be configured with the same value | Changing it on a running system invalidates every token and breaks every product until each is reconfigured |
| `Auth:Tokens:AccessTokenMinutes` | `15` | No | Lifetime of an identity access token | Longer means a stolen token works longer (tokens are not revoked) |
| `Auth:Tokens:ServiceTokenMinutes` | `10` | No | Lifetime of a product's service token (client credentials) | Same |
| `Auth:Tokens:ProductTokenMinutes` | `15` | No | Lifetime of a product token from token exchange; bounds how stale the permissions inside can get after a role or access change | Longer means role changes take longer to apply |
| `Auth:Tokens:RefreshTokenDays` | `30` | No | Lifetime of a refresh token. The web console's "keep me signed in" is capped by this (`AuthService:Session:RememberedDays` is at most 30) | Lowering it signs people out sooner |
| `Auth:Tokens:SigningKeyRotationDays` | `90` | No | How long one signing key signs before the next takes over | Very short values rotate constantly; products cache the JWKS |
| `Auth:Tokens:SigningKeyPrePublishHours` | `24` | No | A new key is published this long before it signs, so products learn it first | If shorter than a product's JWKS cache (`KeyCacheMinutes`, 10 minutes by default in the package), a product can see a signature from a key it has not fetched |
| `Auth:Tokens:SigningKeyPostRetireHours` | `24` | No | A retired key stays published this long so already-issued tokens still validate | Shorter than the longest token lifetime makes valid tokens fail |
| `Auth:Tokens:SigningKeyRefreshSeconds` | `60` | No | How often each instance reloads keys from the database and checks if it is time to rotate (`SigningKeyWorker`; minimum 1) | Larger values delay one instance learning a key published by another |
| `Auth:Applications:SecretGraceHours` / `Auth__Applications__SecretGraceHours` | `24` | No | How long a product's old client secret keeps working after rotation, unless the rotation asks for another time | Longer means a leaked old secret works longer |
| `Auth:ApiKeys:RotationGraceHours` / `Auth__ApiKeys__RotationGraceHours` | `24` | No | How long an API key keeps working after it is rotated out, unless the rotation request gives `graceHours` | Same |

Fixed, not settings: the algorithm (ES256 only), the clock skew accepted when validating (30 seconds), and the service-token audience (`auth-service`).

---

## Login and passwords

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `Auth:LoginProtection:MaxFailedAttempts` / `Auth__LoginProtection__MaxFailedAttempts` | `5` | No | Wrong passwords in a row after which the account is locked | A very high value removes the lockout; `1` locks on the first typo |
| `Auth:LoginProtection:LockoutMinutes` | `15` | No | How long a locked account stays locked | `0` makes the lock last no time (lockout ineffective) |
| `Auth:PasswordReset:TokenMinutes` | `60` | No | How long a reset link works. The email states this value ("30 minutes", "1 hour", "2 days": the largest unit that divides it exactly) | Longer means a leaked link works longer |
| `Auth:PasswordReset:RequestCooldownSeconds` | `60` | No | An account sent a reset link less than this long ago is not sent another | `0` allows email flooding of the address owner |
| `Auth:EmailVerification:TokenHours` | `24` | No | How long a verification link works. The email states this value | |
| `Auth:EmailVerification:ResendCooldownSeconds` | `60` | No | A new verification link is not queued for an account sent one less than this long ago | `0` allows flooding (the tests set it to 0) |
| `Auth:Invitations:TokenDays` | `7` | No | How long an invitation link works. The email states this value | |
| `Auth:Invitations:ResendCooldownSeconds` | `60` | No | After a link is emailed it cannot be emailed again for this long (`429 resend_too_soon` meanwhile) | |

The password length rule (8 to 128 characters, `PasswordPolicy`) is a constant, not a setting. The per-IP login limit is under "Rate limits".

---

## Email

Bound to `Email` (`EmailOptions`). Note the section is `Email`, not `Auth:Email`.

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `Email:Provider` / `Email__Provider` | none. Blank means `File` in Development, `InMemory` in `Testing` | **Required in every other environment.** Missing: `Email:Provider is not configured.` Unsupported: `Email:Provider '<x>' is not supported (use Brevo, File or InMemory).` (startup). Compose: `EMAIL_PROVIDER`, default `Brevo` | Which provider sends mail (case-insensitive). Switching is configuration only | `File` in production writes every email, including the link with its token, to disk and sends nothing. `InMemory` keeps mail in memory only |
| `Email:BrevoApiKey` / `Email__BrevoApiKey` | none | **Required with `Brevo`.** Missing: `Email:BrevoApiKey is not configured.` (startup). Compose: `BREVO_API_KEY` | The API key sent in the `api-key` header to `https://api.brevo.com/v3/smtp/email` | **Secret.** Never logged or stored by the service. Rotate by creating a new key, redeploying, then deleting the old one |
| `Email:FromAddress` / `Email__FromAddress` | `no-reply@localhost` | **Required with `Brevo`**, and must not be blank or the default: `Email:FromAddress must be set to a verified sender for Brevo.` Compose: `EMAIL_FROM_ADDRESS` (default empty) | Sender address of every email | Must be a sender on a domain authenticated in Brevo (SPF, DKIM, DMARC); a personal mailbox lands in spam. An unverified sender makes Brevo answer `400`, so every email fails |
| `Email:FromName` / `Email__FromName` | `Auth Service` | No. Compose: `EMAIL_FROM_NAME` | Display name of the sender | |
| `Email:PublicBaseUrl` / `Email__PublicBaseUrl` | `http://localhost:5080` | Required with the Brevo provider: the service refuses to start unless it is an absolute `https` address (compose sets `https://<APP_HOST>`) | The address links in emails point at; the app there must serve `/verify-email`, `/reset-password` and `/accept-invitation`. A trailing slash is trimmed | The default is the API's development port, not the web console's (5090). A wrong value makes every email link dead |
| `Auth:Discovery:PublicBaseUrl` / `Auth__Discovery__PublicBaseUrl` | none (the request's own address is used) | No, but set it behind a proxy. Compose sets `https://<AUTH_HOST>` | The public address of the API in the discovery document (`/.well-known/auth-service-configuration`): the keys and token addresses a product is told, and the address the console shows beside a new client secret | A value that is not an absolute http or https address is ignored. Without it, the web console, which calls the API by its internal name, would show that internal name |
| `Email:FileDirectory` / `Email__FileDirectory` | `artifacts/emails` under the content root | No. Compose sets `/tmp/emails` | Where the `File` provider writes `.txt` files | Contains live links; development and trial runs only |
| `Email:DispatcherEnabled` / `Email__DispatcherEnabled` | `true` | No | `false`: the worker logs `The email dispatcher is disabled; queued emails will wait.` and exits | Nothing is sent while disabled; for tools such as migrations |
| `Email:DispatchIntervalMilliseconds` / `Email__DispatchIntervalMilliseconds` | `2000` | No | How often the worker looks for due emails (minimum 10) | Very small values poll the database hard |
| `Email:MaxAttempts` / `Email__MaxAttempts` | `5` | No | Tries before a message becomes `Failed` (final) | Low values give up before a provider recovers |
| `Email:RetryDelaySeconds` / `Email__RetryDelaySeconds__0`, `__1`, ... | null, meaning `[30, 120, 600, 3600]` in code | No | Seconds to wait after failed attempt n (index n-1; the last value repeats) | An empty array means no delay (immediate retries) |

---

## Webhooks

Bound to `Auth:Webhooks` (`WebhookOptions`). Webhooks tell a product about subscription changes.

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `Auth:Webhooks:RetryWindowHours` / `Auth__Webhooks__RetryWindowHours` | `24` | No | How long delivery of one event is retried before it is `Failed`; also the "fresh" window when a platform admin resends | |
| `Auth:Webhooks:RetryDelaySeconds` / `Auth__Webhooks__RetryDelaySeconds__0` ... | null, meaning `[60, 300, 1800, 7200, 21600, 43200]` in code | No | Seconds before the next try, indexed by failed attempts; the last repeats | |
| `Auth:Webhooks:SecretOverlapHours` | `24` | No | How long the previous webhook secret keeps signing after rotation. A rotation request may ask 0 to 720 (`MaxSecretOverlapHours`, a constant) | |
| `Auth:Webhooks:TimeoutSeconds` | `10` | No | How long to wait for a product to answer (raised to at least 1) | |
| `Auth:Webhooks:DispatcherEnabled` | `true` | No | `false`: the worker logs `The webhook dispatcher is disabled; queued webhooks will wait.` and exits | Events pile up; after the retry window they become `Failed` |
| `Auth:Webhooks:DispatchIntervalMilliseconds` | `2000` | No | How often the worker looks for due events (minimum 10) | |
| `Auth:Webhooks:NoUrlRecheckSeconds` | `300` | No | How long to wait before looking again at an event whose product has registered no callback URL | |
| `Auth:Webhooks:AllowPrivateAddresses` | `false` | No | Allows callback URLs that are plain `http` or point at private, loopback or link-local addresses | **Never set in production**: it lets a product make the service call into your own network (server-side request forgery). Only for development and tests |

---

## Rate limits

The five policies and their behaviour are explained in [05](05-platform-and-cross-cutting.md#rate-limiting). The per-policy limits are not validated: a limit of
0 or less refuses every request to that endpoint.

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `Auth:RateLimit:Store` / `Auth__RateLimit__Store` | `Postgres` | No. Any other value, **including blank**, stops startup: `Auth:RateLimit:Store is '<x>', which must be 'Postgres' or 'Memory'.` | `Postgres`: counters in `rate_limit_counters`, shared by every instance. `Memory`: per process (development, tests, one instance). `appsettings.Development.json` sets `Memory` | With `Memory` and several instances each limit is multiplied by the number of instances, and a restart clears counts. With `Postgres`, a missing table or database fault makes limited endpoints answer `503` |
| `Auth:RateLimit:CleanupSeconds` | `60` | No. Less than 1 stops startup: `Auth:RateLimit:CleanupSeconds must be at least 1.` | How often expired counters are removed (kept one minute past their window) | |
| `Auth:LoginProtection:IpLimit` | `20` | No | `POST /api/auth/login` requests one IP may make per window, across all accounts | Large values disable the per-IP protection (the tests use 1000) |
| `Auth:LoginProtection:IpWindowMinutes` | `1` | No | Window for the login limit | |
| `Auth:PasswordReset:IpLimit` | `10` | No | Requests one IP may make to forgot and reset (one shared counter) per window | |
| `Auth:PasswordReset:IpWindowMinutes` | `10` | No | Window for the reset limit | |
| `Auth:Applications:TokenIpLimit` | `60` | No | `POST /api/auth/token` requests one IP may make per window, across all products | |
| `Auth:Applications:TokenIpWindowMinutes` | `1` | No | Window for the service-token limit | |
| `Auth:TokenExchange:UserLimit` | `30` | No | Token exchanges one signed-in person may make per window | A looping client could starve the database if set very high |
| `Auth:TokenExchange:UserWindowMinutes` | `1` | No | Window for the exchange limit | |
| `Auth:EmailVerification:ResendIpLimit` | `10` | No | Verification-resend requests one IP may make per window | |
| `Auth:EmailVerification:ResendIpWindowMinutes` | `10` | No | Window for the resend limit | |

---

## Proxy and metrics

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `Auth:Proxy:TrustedProxies` / `Auth__Proxy__TrustedProxies__0` (`__1`...) | empty | Only behind a proxy. A non-IP value stops startup: `Auth:Proxy:TrustedProxies has '<x>', which is not an IP address.` | Exact addresses of reverse proxies whose `X-Forwarded-For` is believed | If left empty behind a proxy, every visitor shares one rate-limit bucket and audit entries name the proxy |
| `Auth:Proxy:TrustedNetworks` / `Auth__Proxy__TrustedNetworks__0` | empty | Same. A non-CIDR value stops startup: `Auth:Proxy:TrustedNetworks has '<x>', which is not a range like 10.0.0.0/8.` Compose sets index 0 from `DOCKER_SUBNET` | CIDR ranges of trusted proxies | **Too wide a range lets any host in it choose its own address** (spoofing audit and rate limits). The API believes only `X-Forwarded-For` |
| `Auth:Proxy:ForwardLimit` / `Auth__Proxy__ForwardLimit` | `1` | Outside 1 to 10 stops startup: `Auth:Proxy:ForwardLimit must be between 1 and 10.` | Entries of `X-Forwarded-For`, counted from the right, that are believed: the number of proxies in front | Too high lets a caller prepend entries that are believed |
| `Observability:AllowedNetworks` / `Observability__AllowedNetworks__0` (`__1`...) | empty (loopback only) | No. A non-CIDR value stops startup: `Observability:AllowedNetworks has '<x>', which is not a range like 10.0.0.0/8.` Compose: `METRICS_ALLOWED_NETWORK` (blank ignored) | Extra networks that may read `/metrics`; everyone else gets `404`. The address checked is the forwarded client address when `Auth:Proxy` is set | A wide range exposes request counts, routes and login-failure counts. Caddy also refuses `/metrics` from the internet |

---

## Web console security headers

Bound to `Security` (`SecurityHeadersOptions`, AUTH-271) in the web app.

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `Security:ScriptPolicyReportOnly` / `Security__ScriptPolicyReportOnly` | `false` | No | `true` sends `script-src` as `Content-Security-Policy-Report-Only`: browsers log what they would block and run it anyway | Left on, injected scripts are not blocked. For a first rollout only |

## Audit

Bound to `Auth:Audit` (`AuditOptions`).

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `Auth:Audit:RetentionDays` / `Auth__Audit__RetentionDays` | `365` | No | Entries older than this are deleted by the retention job | Lowering it deletes history at the next run; no undo. The only deletion the database allows |
| `Auth:Audit:RetentionEnabled` | `true` | No | `false`: the worker logs `The audit retention worker is disabled; old entries will not be deleted automatically.` and exits | Disables all automatic deletion; the table grows. The tests set it `false` |
| `Auth:Audit:RetentionCheckSeconds` | `86400` | No | How often the job runs (minimum 1); it also runs once at startup | |

## Cleanup

Bound to `Auth:Cleanup` (`CleanupOptions`, AUTH-270). Each retention counts from when a row stopped being usable, never from when it was created.

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `Auth:Cleanup:Enabled` / `Auth__Cleanup__Enabled` | `true` | No | `false`: the worker logs `The cleanup worker is disabled; ...` and exits | The token and outbox tables grow without bound. The tests set it `false` |
| `Auth:Cleanup:IntervalSeconds` | `3600` | No | How often a run starts (minimum 1) | |
| `Auth:Cleanup:BatchSize` / `MaxBatchesPerKind` | `500` / `200` | No | Rows per delete statement, and the most statements per kind per run (a larger backlog goes over several runs) | |
| `Auth:Cleanup:RefreshTokenDays` | `7` | No | Days after a refresh token **expires** before it is removed. A revoked token waits for this too, so a replayed old token is still found and revokes its sign-in (reuse detection) | Do not set below 0 |
| `Auth:Cleanup:VerificationTokenDays` / `ResetTokenDays` | `7` / `7` | No | Days after a link was used or expired | |
| `Auth:Cleanup:EmailOutboxDays` | `14` | No | Days after an email was sent (or, for one given up on, written) | |
| `Auth:Cleanup:DeliveredWebhookDays` / `FailedWebhookDays` | `14` / `30` | No | Days after delivery / after the delivery deadline. Failed events stay longer because an admin can resend them | A short value removes events before anyone can resend them |
| `Auth:Cleanup:ExpiredSecretDays` | `7` | No | Days after a previous product secret stopped working; the current secret never expires | |

---

## Web console

The web console (`src/AuthService.Web`) is only a client of the API; it has no database connection.

| Setting (colon / environment) | Default | Required | What it controls and what changes if it differs | Danger |
|---|---|---|---|---|
| `AuthService:Api:BaseUrl` / `AuthService__Api__BaseUrl` | none (Development file: `http://localhost:5080`; compose: `http://api:8080`) | **Required.** Missing: `AuthService:Api:BaseUrl is required: the address of the Auth Service API.` Not absolute http(s): `AuthService:Api:BaseUrl must be an absolute http or https address.` (validated on start) | Where the console's server calls the API (never from the browser) | A wrong address makes every page fail to reach the API |
| `AuthService:Api:TimeoutSeconds` | `15` | No. Outside 1 to 120 stops startup: `AuthService:Api:TimeoutSeconds must be between 1 and 120.` | How long a call to the API may take before it is treated as unreachable | |
| `AuthService:Session:CookieName` | `auth_session` | No (must not be empty; validated on start) | Name of the cookie that carries the session id (never a token) | |
| `AuthService:Session:RememberedDays` | `30` | No. Range 1 to 30 (validated on start) | Session length when the person chose "Keep me signed in"; the API's refresh token lasts 30 days | Cannot exceed the refresh-token lifetime in a useful way |
| `AuthService:Session:DefaultHours` | `12` | No. Range 1 to 720 | Session length otherwise; the cookie also ends when the browser closes | |
| `AuthService:Session:RefreshBeforeSeconds` | `60` | No. Range 0 to 600 | The access token is renewed when it has this many seconds or fewer left | |
| `AuthService:Proxy:TrustedProxies` / `AuthService__Proxy__TrustedProxies__0` | empty | Only behind a proxy. Bad value stops startup: `AuthService:Proxy:TrustedProxies has '<x>', which is not an IP address.` | Reverse proxies believed for `X-Forwarded-For` **and** `X-Forwarded-Proto` | Without it behind Caddy the console sees the wrong address and scheme, and the API sees the console's address for every visitor |
| `AuthService:Proxy:TrustedNetworks` / `AuthService__Proxy__TrustedNetworks__0` | empty | Same. Bad value: `AuthService:Proxy:TrustedNetworks has '<x>', which is not a range like 172.29.200.0/24.` Compose sets index 0 from `DOCKER_SUBNET` | CIDR ranges believed | Too wide lets a visitor choose their address |
| `AuthService:Proxy:ForwardLimit` | `1` | Outside 1 to 10 stops startup: `AuthService:Proxy:ForwardLimit must be between 1 and 10.` | Number of proxies in front of the console | |
| `AuthService:DataProtection:KeysPath` / `AuthService__DataProtection__KeysPath` | none (keys in memory) | No. Compose sets `/keys` on the `webkeys` volume | Folder where the keys that protect the cookies are kept, so a restart or update does not make every cookie unreadable | Without it every restart invalidates all cookies. The keys sit unencrypted in the folder (compose accepts this; they protect only an opaque session id). Back up or keep the volume |

Not settings: the session store is in memory (`MemorySessionStore`), so a console restart ends every session and only one instance can serve
a person's session; cookie `SameSite` is `Lax`; the cookie is `Secure` except in Development.

---

## Deployment variables for the compose stack

These go in `deploy/.env` (copy `deploy/.env.example`; git ignores `.env`). Compose refuses to start when a `:?` variable is empty.

| Variable | Default | Required | Effect | Danger |
|---|---|---|---|---|
| `AUTHSERVICE_VERSION` | none | **Yes** (`:?`) | The image tag for the API, web console and migrate job. Example `1.0.0` (a tag `v1.0.0` is `1.0.0`) | Pin an exact version so an update is deliberate. To roll back, set the previous version, `docker compose pull && up -d`; migrations are not undone |
| `AUTHSERVICE_REGISTRY` | `ghcr.io/ghazi1567` | No | Registry prefix of the three images (`authservice-api`, `-web`, `-migrate`) | Only change to use your own registry |
| `AUTH_HOST` | none (`.env.example`: `auth.example.com`) | **Yes** | Name Caddy serves the API on and gets a certificate for; products fetch keys from `https://<AUTH_HOST>/.well-known/jwks.json` | DNS must point at the server and ports 80 and 443 be open |
| `APP_HOST` | none (`.env.example`: `app.example.com`) | **Yes** | Name of the web console; also becomes `Email__PublicBaseUrl` (`https://<APP_HOST>`) | Wrong value makes email links dead |
| `ACME_EMAIL` | none | **Yes** (`:?`, even behind a tunnel) | Address Let's Encrypt writes to about certificates. Compose checks it before it merges `docker-compose.tunnel.yml`, so it must hold an address behind a tunnel too, where it is unused | |
| `COMPOSE_FILE` | none | For a tunnel | `docker-compose.yml:docker-compose.tunnel.yml` makes every `docker compose` command use the tunnel file too (no Caddy, no published ports) | Without it the Caddy stack starts, which needs ports 80 and 443 |
| `TUNNEL_TOKEN` | none | With the tunnel file (`:?`) | The Cloudflare tunnel's token, passed to `cloudflared` | **Secret**: whoever has it can run the tunnel |
| `CLOUDFLARED_VERSION` | `2025.11.1` | No | Image tag of `cloudflare/cloudflared` | An untested newer tag may change behaviour |
| `POSTGRES_PASSWORD` | none (`.env.example`: `change-me`) | **Yes** | Password of the bundled `db` container's `auth` user, and inside the API and migrate connection strings | **Secret.** Replace `change-me` (`openssl rand -base64 24`). Changing it after the volume exists does not change the stored password |
| `AUTH_KEY_ENCRYPTION_KEY` | none | **Yes** | Becomes `Auth__Tokens__KeyEncryptionKey` | **Secret.** See "Connection and secrets" |
| `AUTH_ISSUER` | `AuthService` | No | Becomes `Auth__Tokens__Issuer` | Products must match |
| `EMAIL_PROVIDER` | `Brevo` | No | Becomes `Email__Provider` (`Brevo`, or `File` for a trial) | `File` sends nothing |
| `BREVO_API_KEY` | empty | With Brevo | Becomes `Email__BrevoApiKey` | **Secret** |
| `EMAIL_FROM_ADDRESS` | empty | With Brevo | Becomes `Email__FromAddress` | Empty with Brevo stops the API at startup |
| `EMAIL_FROM_NAME` | `Auth Service` | No | Becomes `Email__FromName` | |
| `DOCKER_SUBNET` | `172.29.200.0/24` | No | The stack's fixed private network, and the trusted proxy network for both apps (`Auth__Proxy__TrustedNetworks__0`, `AuthService__Proxy__TrustedNetworks__0`) | Change only if it clashes with another network. The whole subnet is trusted for `X-Forwarded-For` |
| `METRICS_ALLOWED_NETWORK` | empty | No | Becomes `Observability__AllowedNetworks__0`. Blank means only the stack itself; the proxy never passes `/metrics` on | A wide range exposes metrics to that range |

Fixed values the compose file sets (not variables; edit the file to change them):

| Container | Environment variable | Value | Why |
|---|---|---|---|
| `db` | `POSTGRES_DB`, `POSTGRES_USER` | `auth`, `auth` | Database and user names |
| `api` | `Email__PublicBaseUrl` | `https://${APP_HOST}` | Links in emails point at the console |
| `api` | `Auth__Discovery__PublicBaseUrl` | `https://${AUTH_HOST}` | The API's public address in the discovery document |
| `api` | `Logging__LogLevel__Microsoft.EntityFrameworkCore` | `Warning` | Drops the line per database command |
| `api` | `Logging__LogLevel__Microsoft.AspNetCore.DataProtection` | `Error` | The service stores nothing with data protection; the warning is noise |
| `api` | `Email__FileDirectory` | `/tmp/emails` | Only used when `EMAIL_PROVIDER=File`; the container user cannot write under `/app` |
| `web` | `AuthService__Api__BaseUrl` | `http://api:8080` | The API by its name inside the stack |
| `web` | `AuthService__DataProtection__KeysPath` | `/keys` | Volume `webkeys` |
| `web` | `Logging__LogLevel__Microsoft.AspNetCore.DataProtection` | `Error` | Unencrypted-keys warning left out |
| `api`, `web` | `ASPNETCORE_ENVIRONMENT` | `Production` | Set in the `Dockerfile` |
| `caddy` | `AUTH_HOST`, `APP_HOST`, `ACME_EMAIL` | from `.env` | Read by `deploy/Caddyfile` |

Other helper inputs: `deploy/check.sh <api host> <web host>` takes optional environment `CHECK_INSECURE` and `CHECK_IP`;
`deploy/backup.sh` reads `deploy/.env` through `docker compose --env-file .env`. `deploy/docker-compose.build.yml` builds the images from source
instead of pulling them (still set `AUTHSERVICE_VERSION` to any name, for example `local`).

---

## What the tests override

`AuthApiFactory` (`tests/AuthService.IntegrationTests/Infrastructure/AuthApiFactory.cs`) boots the API in the `Testing` environment (no
`appsettings.Development.json`; the fixed test encryption key; `InMemory` email) and sets these, so a test that expects a production
default must set it back through `HarnessOptions.Settings`:

| Setting | Test value | Production default |
|---|---|---|
| `ConnectionStrings:AuthDb` | the test database | none |
| `Email:DispatchIntervalMilliseconds` | `50` | `2000` |
| `Email:RetryDelaySeconds:0` | `0` | `[30,120,600,3600]` |
| `Email:PublicBaseUrl` | `https://app.test` | `http://localhost:5080` |
| `Auth:EmailVerification:ResendCooldownSeconds` | `0` | `60` |
| `Auth:RateLimit:Store` | `Memory` | `Postgres` |
| `Auth:LoginProtection:IpLimit` | `1000` | `20` |
| `Auth:TokenExchange:UserLimit` | `1000` | `30` |
| `Auth:Audit:RetentionEnabled` | `false` | `true` |
| `Auth:Cleanup:Enabled` | `false` | `true` |
| `Auth:Webhooks:DispatcherEnabled` | `false` | `true` |
| `Auth:Webhooks:DispatchIntervalMilliseconds` | `50` | `2000` |
| `Auth:Webhooks:AllowPrivateAddresses` | `true` | `false` |
| `Auth:PasswordReset:RequestCooldownSeconds` | `0` | `60` |
| `Auth:PasswordReset:IpLimit` | `1000` | `10` |

Logging is replaced by a capturing provider at trace level.

---

## Settings that are not settings

Constants in code that look configurable but are not:

- Password length: 8 to 128 characters (`PasswordPolicy`).
- Paging: default 50, maximum 200 (`PageQuery`, `AuditPaging`, `WebhookAdminEndpoints`).
- Batch size: 50 emails or webhooks per dispatcher pass; Brevo HTTP timeout 15 seconds; readiness database timeout 5 seconds.
- JWT: ES256 only, 30 seconds of clock skew, service-token audience `auth-service`, built-in product audience `auth-service-tenant`.
- Webhook maximum secret overlap: 720 hours (`WebhookOptions.MaxSecretOverlapHours`).
- The `AuthService.Authorization` package that products install is configured in the product's code with
  `AddAuthServiceAuthorization(options => ...)` (`Audience`, `Issuer`, `JwksUrl` required; `AllowInsecureJwksUrl` false, `KeyCacheMinutes` 10,
  `MinimumRefreshSeconds` 30, `ClockSkewSeconds` 30), not from this service's configuration.

---

## Unknowns and assumptions to confirm

- **Assumption (confirm):** `Auth__Proxy__ForwardLimit` and the other `Auth:Proxy` settings behave as the README describes when passed as environment variables; verified from code and tests, not run.
- **Unknown:** whether the numeric settings without validation (limits, windows, cooldowns, lifetimes) should be validated at startup like `Auth:RateLimit`; today `0` or negative values are accepted.
- **Settled (AUTH-266):** the expiry text in emails follows `Auth:EmailVerification:TokenHours`, `Auth:PasswordReset:TokenMinutes` and `Auth:Invitations:TokenDays`; and the Brevo provider refuses to start with a missing, relative or plain-http `Email:PublicBaseUrl`. The File and InMemory providers still accept the localhost default, which is meant for development and tests.
- **Assumption (confirm):** `AllowedHosts: "*"` is acceptable because the reverse proxy filters hosts; no host filtering exists in the apps.
- **Assumption (confirm):** the web console runs as a single instance, because sessions are in memory (`MemorySessionStore`); no setting selects another store.
- **Unknown:** the effect of a command-line argument form for settings; the repository never uses it, so it is described only as the standard ASP.NET Core behaviour.
- **Unknown:** the effect of `Auth:Tokens:SigningKeyPrePublishHours` being shorter than a product's JWKS cache; the package default is 10 minutes (`KeyCacheMinutes`), so the shipped defaults are safe, but the relationship is not enforced anywhere.
