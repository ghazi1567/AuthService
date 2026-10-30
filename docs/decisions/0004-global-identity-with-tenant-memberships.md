# 0004. One global identity with tenant memberships

- Status: Accepted
- Date: 2026-09-26 (decided in Jira AUTH-117); recorded here 2026-10-31
- Tickets: AUTH-117, AUTH-8 (TEN-01)

## Context

The first draft of the data model put every user inside exactly one tenant (`User.TenantId`, email unique per
tenant). That contradicted two things in the spec: a signup test expecting a 409 for an email "already registered to
another tenant", and login taking only an email and password with no tenant. It also did not fit the single
sign-on goal for people who work with more than one business (agencies, freelancers, owners of several companies).

## Decision

- A **User** is one global identity: one email, unique across the platform and case-insensitive, and one password.
- A **TenantMembership** joins a user to a tenant and carries its own status (Invited, Active, Disabled).
- **Roles and product access attach to the membership**, not the user (`MembershipRole`, and later
  `MembershipApplicationAccess`). A role belongs to one tenant, and the database refuses to link a membership and
  a role from different tenants.
- Login does not choose a tenant. It returns the user's memberships, and the tenant is chosen at token exchange,
  so every product token carries exactly one tenant.

## Alternatives considered

- **Globally unique email, one tenant per user.** Cheapest now, but a person in two businesses would need a second
  email, and changing this later is very expensive.
- **Email unique per tenant.** Login would also need a tenant, and a person would have one password per tenant.
  Does not fit single sign-on.

## Consequences

- Signup (TEN-01) creates a tenant, a user and an Active Owner membership; an existing person creates further
  tenants through TEN-09 instead.
- A signup with an email that already exists returns 409 and points to login, which reveals that the email is
  registered. The spec accepts this for signup; password reset (TEN-10) must not reveal it.
- The `Entity Fields` and `Modules & Tasks` tabs in older copies of the workbook still show the first draft.
  Jira and the Scope Document (v1.0) are the current source of truth.
