# 0005. The Auth Service is a product of its own

- Status: Accepted
- Date: 2026-11-01
- Tickets: AUTH-12 (TEN-05), AUTH-20 (SUB-03), AUTH-21 to AUTH-25 (ROL-01 to ROL-05), AUTH-14 (TEN-07), AUTH-103 (TEN-08), AUTH-28 (TOK-03)

## Context

The login (identity) token names a person and nothing else, because a person can belong to several tenants (decision 0004). The
tenant-scoped endpoints of the Auth Service itself (roles, members, subscriptions, invitations, the current tenant) must know
which tenant the caller is working in, and must never take it from the path, query string or body (TEN-05, TOK-03). The specs
describe that tenant only as the `tid` claim of an "Application-scoped token", which exists only for products. The scope document
(risk R-01) allowed interim guards until the permission middleware existed, but an interim would have meant rewriting every
endpoint.

## Decision

The Auth Service is registered as a product of its own (the **built-in product**), and tenant admins use the ordinary token
exchange (TOK-01) to get a token for it. Its endpoints read the tenant from `tid` and check `permissions` claims by level, exactly
as any other product does.

- It has a fixed id, key `auth-service` and audience `auth-service-tenant`, is stored at startup, has no client credentials, is
  not offered to tenants, and cannot be changed through the admin API. Its permission catalog is defined in code and synced
  add-only.
- It needs no subscription and no seat: any Active member of a tenant can get a token for it. What they may do is decided by
  their roles, and the built-in Owner role holds Full on its whole catalog.

## Alternatives considered

- **Interim guard.** Identity token plus a tenant chosen by a request header, checked against the caller's membership and Owner
  role. Quick and allowed by the scope document, but the tenant would come from the request until TOK-03, and every endpoint would
  be rewritten later.
- **A second kind of tenant-scoped identity token.** Simple, but a new token type beside the product token the specs already
  define, with its own claims and checks to keep in step.

## Consequences

- Tenant-scoped endpoints are protected with `RequireTenantContext()` or `RequireTenantPermission(key, level)`; each new endpoint
  adds its permission to `BuiltInApplication.Permissions`.
- ROL-01 must treat the built-in product as always subscribed when it checks which permissions a role may grant.
- Like every product token, a tenant-admin token lives 15 minutes and is not revoked early, so a disabled membership or a changed
  role applies at the next exchange.
- Deployment: the first start after this change inserts the built-in row; startup fails with a clear message if the key
  `auth-service` is already registered to another application.
