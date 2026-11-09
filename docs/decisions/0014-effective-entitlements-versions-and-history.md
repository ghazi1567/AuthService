# 0014. A tenant's limits are computed, versioned only when they change, and every change is kept in a separate history

- Status: Accepted
- Date: 2026-11-10
- Tickets: AUTH-280 (ENT-03), epic AUTH-277

## Context

Products need plans with limits that platform admins control (the proposal "Plans and entitlements for the Auth Service"). ENT-01 added limit
definitions and ENT-02 plans. Here a tenant's subscription gets a plan and per-tenant overrides, and the Auth Service must say what the tenant's
limits are, in a way a product can store and keep up to date from events that may arrive twice or out of order (ENT-04). The proposal
contradicted itself on when a version rises, on the seat-limit endpoint, and on what a subscription without a plan gets; the choices were agreed
on 2026-11-10 (epic AUTH-277).

## Decision

- **Effective value = override, else plan value, else the limit's default.** Every limit of the product is always in the set, so a product never
  has to guess what a missing key means. A subscription with no plan gets every default.
- **The version rises only when the set tells the product something new:** a value, or whether a limit is overridden. A plan edit that a tenant's
  override hides does not touch that tenant. Adding a limit changes every tenant (one more key), relabelling one changes nobody. A new
  subscription starts at version 1.
- **One sequence numbers every version change** (`entitlement_change_seq`, column `entitlements_change_seq`), so products can page through all
  their tenants in change order and ask for what changed since a point (ENT-05).
- **`seats` is the one limit the Auth Service acts on.** When a product defines it, its effective value is written to `maxSeats` (what token
  exchange and access grants check) after every change, and the product's existing seat-limit call (and a seat limit sent while subscribing)
  becomes a `seats` override with the reason "Seat limit set by the product." Products that never define `seats` keep setting `maxSeats` as
  before. Because `maxSeats` is at least 1, a `seats` value of 0 is refused everywhere.
- **History is its own table** (`entitlement_changes`): who, when, which product, tenant, plan and limit, the old and new value as JSON, and the
  reason. Override reasons are free text (up to 200 characters), so they live only there, read only by platform admins; the audit log keeps its
  short codes (decision 0013) and gets `auth.subscription.plan_assigned`, `override_set` and `override_cleared` with the product key.
- **A plan that was ever assigned can never be deleted** (`plans.ever_assigned`), only deactivated, so the history always names a plan that exists.

## Alternatives considered

- **Raise every version on every related change** (what one line of the proposal said). Simpler, but a plan edit would wake every product
  store for tenants whose numbers did not move, and the proposal's own acceptance item 5 says an override keeps its value.
- **Reject the seat-limit call once a `seats` limit exists** (the other line of the proposal). Breaks products that already call it, against
  the proposal's own "additive" principle.
- **Put the reason in the audit log.** Breaks the rule that audit details are short codes that can never carry personal data.

## Consequences

- A plan edit recomputes every subscription on that plan inside the admin's request; with thousands of tenants that request takes longer.
  Events (ENT-04) are written in the same transaction and delivered in the background.
- The history is never deleted yet (the proposal asks for at least 24 months); a retention job can be added later.
- A product that sets seats through the API now sees its number as an override in the portal, with "set by the product".
