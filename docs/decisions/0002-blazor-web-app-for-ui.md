# 0002. Build the UI as a Blazor Web App

- Status: Accepted (the `AuthService.Web` project does not exist yet)
- Date: 2026-10-30
- Tickets: none yet

## Context

The service currently has an API only. It will also need user-facing pages (login, registration,
password reset, MFA) and an admin console for tenants, roles, permissions and keys. Login pages handle
credentials and sessions, so they must be same-origin and keep tokens out of browser JavaScript.
The admin console is form- and table-heavy and used by a small number of people. The team works in .NET.

## Decision

Build the UI as a Blazor Web App in a separate project, `src/AuthService.Web`:

- login and account pages use static server-side rendering with cookie authentication;
- the admin console uses interactive server rendering.

The API stays headless, because other products in the platform call it. The UI is one more client of
the same application logic.

## Alternatives considered

- **React, Angular or another SPA.** Better ecosystem, hiring pool and tooling for highly interactive or
  widely embedded UIs. Not chosen because it adds a second language, build pipeline and deployment, plus
  CORS and browser-side token handling, for a UI that is mostly forms behind a login.

## Consequences

- One language and toolchain, shared models and validation, and server-side authentication with the
  usual `[Authorize]` policies.
- Interactive server rendering holds a live connection per user, which affects scaling and flaky networks.
- Fewer ready-made components and front-end developers to hire. Revisit this if the UI must be embedded
  in other products, becomes public and design-heavy, or a dedicated front-end team takes it over.
  Keeping the API headless makes that swap possible without changing the backend.
