# 0015. Entitlement change numbers become visible in the order they were given out

- Status: Accepted
- Date: 2026-11-11
- Tickets: AUTH-282 (ENT-05), epic AUTH-277

## Context

ENT-05 lets a product ask for every tenant whose entitlements changed since a change number it already holds
(`GET /api/auth/applications/me/entitlements?after=N`). Change numbers come from one sequence (decision 0014). A sequence hands out numbers when a
statement runs, not when its transaction commits. So two changes to the same product could take 10 and 11, and 11 could commit first. A product
reading at that moment would see 11, remember it, and never ask for 10 again: a tenant's new limits would be lost until its next change.

## Decision

- **A product's change numbers are given out one transaction at a time.** `EntitlementState.CommitAsync` locks the subscription rows it is about to
  change, then takes a transaction-scoped advisory lock for the product (`entitlement-change:<applicationId>`), then raises versions and takes the
  numbers. The lock is held until the transaction ends, so a second change to the same product gets its number only after the first has committed
  or rolled back. What a reader sees is therefore always every number up to some point, with gaps only for rolled-back changes.
- **Rows first, then the product's lock.** A transaction never waits for a subscription row while holding the product's lock, so the lock cannot
  join a deadlock with the row locks other changes take.
- **Subscriptions from before ENT-03 get a number.** Migration `BackfillEntitlementChangeNumbers` gives each subscription still at change number 0
  version 1 and its own number, so the read can page through every subscription by number alone.

## Alternatives considered

- **Return only changes older than a few seconds.** Simple, but only usually right: a slow transaction can still commit late.
- **Filter by transaction ids (`pg_snapshot_xmin`).** Exact only if numbers are taken in transaction-id order, which they are not.
- **Have products re-read with an overlap.** Pushes a subtle rule onto every product, and still loses a change that commits after the overlap.

## Consequences

- Entitlement changes to one product are serialised from the moment numbers are taken until commit. That part of each transaction is short (an
  event insert, an audit row), but a plan edit that recomputes thousands of tenants holds the product's lock for its last statements.
- Changes to different products never wait for each other.
- `ENT05_ASecondChangeWaitsForTheFirstToCommitSoChangeNumbersFollowCommitOrder` proves the wait, and fails if the lock is removed.
