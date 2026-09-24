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
