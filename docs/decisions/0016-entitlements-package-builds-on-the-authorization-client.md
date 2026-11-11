# 0016. The entitlements package builds on the authorization package's client and is released after it

- Status: Accepted
- Date: 2026-11-11
- Tickets: AUTH-284 (ENT-07), epic AUTH-277

## Context

Products need a small library to keep each organisation's plan and limits (ENT-04 events, ENT-05 reads) without each team writing webhook
verification, version ordering and catch-up themselves. The epic agreed that it builds on `IAuthServiceClient` from
`AuthService.Authorization` (decision 6 of AUTH-277), so a product configures its Auth Service address and secret once and the service
token is cached in one place. That client only had fixed calls and kept its connection internal.

## Decision

- **One public addition to the authorization package:** `IAuthServiceClient.SendAsync(method, path, content)` calls any Auth Service endpoint
  with the product's service token, renewed once if refused, exactly like the existing calls. It is a default interface method that throws
  `NotSupportedException`, so other implementations (test fakes) keep compiling. This is a minor release: **AuthService.Authorization 1.2.0**.
- **`AuthService.Entitlements`** (new) depends on `AuthService.Authorization` >= `MinimumAuthorizationVersion` (1.2.0). Its own version comes
  from `EntitlementsVersion`; `Version` is what the dependency is written with, because a project reference packs the dependency at the
  referenced project's version.
- **`AuthService.Entitlements.Testing`** (new) gives products a fake client and signed webhooks, released with the same version.
- The store keeps the highest version only; a missing key is the limit's default, never unlimited; an event with a newer `schemaVersion` is
  answered `400` so it stays visible as failed instead of being dropped.
- **Release order:** tag `authorization-v1.2.0`, wait for it, then tag `entitlements-v1.0.0` (`publish-entitlements-package.yml`). The nuget.org
  step needs a trusted publishing policy for the new workflow file.
- **Update (2026-11-11):** the `authorization-v1.2.0` run reached GitHub Packages but stopped at the install check, because the sample
  product now also installs the entitlements packages and nothing had published them yet. The check now packs them from the same commit.
  A version is never pushed twice, so the first public release with `SendAsync` is **1.2.1** (1.2.0 stays on GitHub Packages only), and
  `MinimumAuthorizationVersion` is 1.2.1 so the entitlements packages never point at a version nuget.org does not have.

## Alternatives considered

- **A second HTTP client inside the entitlements package.** Two places to configure the address and secret, and two token caches.
- **Make `ProductConnection` public.** Exposes internals that would then be hard to change.
- **Put the entitlements code in the authorization package.** Every product would carry webhook and store code it may not use.

## Consequences

- A product upgrading to entitlements must also take Authorization 1.2.1.
- Duplicate events are dropped per process; a product on several instances relies on version ordering in its own store.
