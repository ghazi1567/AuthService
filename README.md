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
