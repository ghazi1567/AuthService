# 0003. Apply EF Core migrations explicitly, never on startup

- Status: Accepted
- Date: 2026-10-30
- Tickets: AUTH-33

## Context

The database schema is owned by EF Core migrations. Tests need a real schema, and production must run
the same schema changes that tests ran. Running migrations automatically when the API starts is
convenient but runs schema changes with application credentials, can race when several instances start
together, and hides when a change is applied.

## Decision

- The schema comes only from migrations. `EnsureCreated()` is never used.
- The API never migrates on startup. Migrations are applied explicitly (`dotnet ef database update`, or a
  deployment step).
- The test database is built by applying the same migrations once to a template database, which each test
  class then copies.
- `TST_TC2` fails if the model changes without a matching migration.

## Alternatives considered

- **Migrate on startup.** Simple for development. Not chosen for the reasons above.
- **`EnsureCreated()` in tests.** Fast, but tests would run against a schema that production never sees,
  and it hides missing migrations.

## Consequences

- Developers must run the migration command after pulling a new migration. The README documents it.
- A deployment pipeline needs an explicit migration step.
- Tests exercise the real migration path, so a broken migration is caught before release.
