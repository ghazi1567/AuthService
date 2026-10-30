# 0001. Use PostgreSQL 17 as the database

- Status: Accepted
- Date: 2026-10-30
- Tickets: AUTH-33

## Context

AuthService owns its own database and is deployed on its own, so the engine is chosen once for this
service. The data model relies on case-insensitive emails (`citext`) and, later, tenant-scoped access
control where database-level isolation (row-level security) is attractive. The service is built with
EF Core 10 (Npgsql provider) and tested against a real database in a throwaway container.

## Decision

Use PostgreSQL 17 through EF Core with the Npgsql provider and snake_case naming. The same major
version is used in tests, local development and production.

## Alternatives considered

- **SQL Server.** Strong fit if the organisation already runs and supports it. Not chosen because there
  is no existing SQL Server estate recorded for this project, it adds per-instance licensing cost for a
  service that is deployed on its own, and its test container is much heavier. Its default collations
  are case-insensitive, so it would not need `citext`.

## Consequences

- The provider, the `citext` extension, snake_case naming, the Baseline migration and the Testcontainers
  fixture are PostgreSQL-specific. Switching later gets more expensive with every migration added.
- The `citext` extension must be creatable in production (trusted on PostgreSQL 13+ for a database owner;
  some managed services restrict it).
- The test image tag is pinned in `TestSettings.cs`. Alpine uses a different C library (musl) than the
  usual Debian image, so sort order and case folding can differ. Prefer the image flavour that matches
  production.
- Revisit this decision if an existing SQL Server platform or hosting target is mandated.
