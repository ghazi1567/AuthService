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
| `Auth:Tokens:SigningKeyPem` | EC (P-256) private key, in PEM form, that signs tokens. **Required outside Development and Testing**: the API refuses to start without it. Pass it as a secret (`Auth__Tokens__SigningKeyPem`), never in a committed file. |
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
| `Auth:LoginProtection:IpLimit` / `IpWindowMinutes` | How many login requests one IP may make per window, across all accounts, before getting 429. Default 20 per minute. |
| `Auth:EmailVerification:TokenHours` | How long a verification link works. Default 24. |
| `Auth:EmailVerification:ResendCooldownSeconds` | An account sent a link less than this long ago is not sent another. Default 60. |
| `Auth:EmailVerification:ResendIpLimit` / `ResendIpWindowMinutes` | How many resend requests one IP may make per window before getting 429. Default 10 per 10 minutes. |

Generate a signing key with `openssl ecparam -name prime256v1 -genkey -noout`. In Development and Testing a throwaway key
is created at startup, so tokens stop working when the process restarts. Key rotation and publishing public keys (JWKS)
arrive with TOK-02.

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

### Calling protected endpoints

Endpoints that act as the logged-in person (for example `GET /api/auth/users/me`) take the **identity access token** that login
and refresh return, as `Authorization: Bearer <token>`. The service checks the signature (with its own signing key), the
issuer and the expiry (with 30 seconds of clock skew), and accepts only the algorithm it signs with.

- No token, a malformed one, an expired one, or one signed by another key or issuer gets **401** with code `unauthorized` and a
  `WWW-Authenticate: Bearer` header.
- A correctly signed token that carries an audience (a product token, TOK-01) is refused with **403** on identity endpoints.
- Who the caller is comes only from the token's `sub` claim, never from the path or query string, so a request cannot ask for
  someone else's data by passing an id.

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
