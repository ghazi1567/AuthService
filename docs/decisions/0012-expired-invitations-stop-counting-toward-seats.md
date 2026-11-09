# 0012. An expired invitation stops counting toward seats, and an invitation can be withdrawn

- Status: Accepted
- Date: 2026-11-10
- Tickets: AUTH-269 (FIX-04)

## Context

A person who has been invited and has product access holds a seat, because seats are counted from the access rows of memberships that are
Active or Invited. An invitation that nobody accepts used to hold that seat for ever: nothing expired it, there was no way to withdraw it,
and the only way out was to disable the pending membership. A tenant with a full product could be blocked from inviting real people by
invitations sent months ago to addresses that never answered.

Two things were needed: a way for an admin to take an invitation back, and a rule for what an expired invitation does to the seat count.
The second one could be a worker that removes expired invitations, or a change to how seats are counted.

## Decision

- **Seats are counted only for invitations that can still be accepted.** The seat query (`SubscriptionResponses.SeatHolders`, used by
  every count: inviting, granting access, re-enabling a member and the `seatsUsed` shown to admins) takes the current time and leaves out
  an Invited membership whose invitation has expired. There is no background job. The seat is free the moment the invitation expires, to
  the second, with nothing to run or to fail.
- **An expired invitation stays in the list**, flagged `isExpired`, as before, because it is the one an admin may want to send again.
  **Sending it again takes the seat back**, so the resend runs the same check as a new invitation, under the same row locks, and answers
  **409** `no_seat_available` (naming the full products) when a product filled up in the meantime. A live invitation already holds its
  seat and is not checked.
- **An admin can withdraw a pending invitation** with `DELETE /api/auth/tenants/me/invitations/{membershipId}` (`members.invite` at
  `Full`, the permission that sends one). It removes the pending membership with its roles, access and invitation, so the link stops
  working (**410**) and the seats are freed at once, and the same address can be invited again (a membership that stayed would make that
  **409** `already_member`). A person who exists only because of the invitation (no password, no other organisation) is removed too;
  anyone with an account or another organisation is left as they are. Withdrawing is audited (`auth.invitation.revoked`: the admin, the
  tenant, the membership id). An invitation already accepted is **409** `invitation_not_pending`, and another tenant's is **404**.
- The console gets a **Withdraw** action on the invitations page, behind a confirmation that names the person.

## Alternatives considered

- **A background sweep** that deletes (or marks) expired invitations and their memberships. It would remove expired invitations from the
  list, which admins use to resend them, it needs a worker that can be off or fail (an invitation would then hold its seat again, silently),
  and the freed seat would arrive up to one sweep interval late. Not chosen. Counting by time needs no state to keep correct.
- **A `Revoked` membership status.** Keeps a record in the membership table, but every query that treats a status as "a member" then
  needs to know about it, a migration is needed, and the address still could not be invited again without special cases. The audit log
  already records the withdrawal. Not chosen.
- **Counting an expired invitation's seat until an admin acts.** The old behaviour. Not chosen: it is the problem being fixed.

## Consequences

- Seat counts depend on the time of the request, so tests that count seats use the controllable clock. A count shown to an admin can go
  down between two page loads without anyone doing anything, which is the intent.
- A membership that is Invited and has **no invitation row** is still counted. The API cannot create one, so this only protects
  hand-made data from silently vanishing out of the count.
- Resending an expired invitation can now fail where it used to succeed (the product is full). The console explains it and offers the
  two ways out: free a seat, or withdraw the invitation and invite the person again with fewer products.
- Because an expired invitation holds no seat, a product's `seatsUsed` can be lower than the number of rows in `application_access`
  for Invited members. Operators counting seats in SQL must apply the same rule (the reference gives the query).
- If invitations ever need to disappear from the list after a time, a sweep can be added on top without changing the counting rule.
