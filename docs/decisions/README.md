# Decision records

Short records of decisions that outlive a single pull request: the choice, the reasons, and what was
given up. The commit message explains *why a change was made*; a decision record explains *why the
project works this way*.

## When to write one

Write a record when a choice is expensive to reverse or someone will later ask "why did we do it this way?"
(database, hosting, auth model, UI technology, rules the whole team must follow). Do not write one for
routine code changes.

## How

1. Copy the template below to `NNNN-short-title.md`, using the next number.
2. Keep it to one page. Link the ticket (e.g. AUTH-33) and any related records.
3. Never rewrite an accepted record to change the decision. Add a new record, and set the old one's
   status to `Superseded by NNNN`.

## Template

```markdown
# NNNN. Title

- Status: Proposed | Accepted | Superseded by NNNN
- Date: YYYY-MM-DD
- Tickets: AUTH-nn

## Context
What forces the decision? What is the situation and what are the constraints?

## Decision
What we chose, in one or two sentences.

## Alternatives considered
What else was an option, and why it was not chosen.

## Consequences
What becomes easier, what becomes harder, and what must be revisited if circumstances change.
```

## Index

| # | Decision | Status |
|---|---|---|
| [0001](0001-use-postgresql.md) | Use PostgreSQL 17 as the database | Accepted |
| [0002](0002-blazor-web-app-for-ui.md) | Build the UI as a Blazor Web App | Accepted |
| [0003](0003-migrations-applied-explicitly.md) | Apply EF Core migrations explicitly, never on startup | Accepted |
| [0004](0004-global-identity-with-tenant-memberships.md) | One global identity with tenant memberships | Accepted |
| [0005](0005-auth-service-as-its-own-product.md) | The Auth Service is a product of its own | Accepted |
| [0006](0006-signed-in-session-kept-on-the-server.md) | The signed-in session is kept on the server | Accepted |
| [0007](0007-hand-the-token-to-a-product-with-a-form-post.md) | Hand a product its token with a form post | Accepted |
| [0008](0008-rate-limit-counters-in-postgresql.md) | Keep rate-limit counters in PostgreSQL, and fail closed | Accepted |
| [0009](0009-publish-the-authorization-package-to-github-packages.md) | Publish the authorization package to GitHub Packages from a version tag | Accepted |
| [0010](0010-publish-the-authorization-package-publicly-on-nuget-org.md) | Also publish the authorization package publicly on nuget.org, under MIT | Accepted |
| [0011](0011-one-client-secret-and-one-address-for-a-product.md) | A product is set up from one address and one client secret | Accepted |
| [0012](0012-expired-invitations-stop-counting-toward-seats.md) | An expired invitation stops counting toward seats, and an invitation can be withdrawn | Accepted |
