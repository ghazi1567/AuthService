# 0013. Suspending an organisation is an audited admin action, checked on every tenant-admin call

- Status: Accepted
- Date: 2026-11-10
- Tickets: AUTH-268 (FIX-03)

## Context

An organisation could be suspended only by editing the `tenants` table. That left no trace of who did it or why, and the suspension was
patchy: token exchange, API-key validation and subscribing refused a suspended organisation, but a tenant-admin token issued before the
suspension kept working until it expired (up to 15 minutes), and someone holding an invitation link could still join. The console told
people to "ask the platform administrator", who had no way to do it.

## Decision

- **A platform admin suspends and reactivates through the API** (`PUT /api/auth/admin/tenants/{id}/status`, `{"status", "reason"}`), and the
  platform console has an Organisations page for it, with a confirmation. `GET /api/auth/admin/tenants` lists organisations with their
  status. Setting the status an organisation already has changes nothing and is not audited.
- **The reason is one of a short list of codes** (`non_payment`, `abuse`, `security`, `customer_request`, `resolved`, `other`), and the audit
  entry (`auth.tenant.suspended` / `auth.tenant.reactivated`) records the admin, the organisation and that code. It is not free text: the audit
  log's detail column holds only short codes by design, so that it can never carry a name, an address or any other personal data, and a
  free-text reason would break that rule.
- **Every tenant-admin request checks the organisation's status**, with one primary-key read, instead of trusting the token. A suspension
  therefore stops tokens already issued at once, and a reactivation lets the same token work again. The refusal is **401** with the problem
  code `tenant_suspended` (a token for a suspended organisation is no longer accepted), not 403: the web console already answers a 401 by
  dropping its cached token and reloading, and the token exchange it then makes answers `403 tenant_suspended`, which it already turns into
  "Acme Ltd is suspended". A 403 would have shown every console page a "you don't have permission" message instead.
- **Also refused for a suspended organisation**, with `tenant_suspended`: accepting an invitation (**403**, checked before the link is used
  up, so it works again after reactivation) and a product changing the organisation's seat limit (**409**, like subscribing). Token
  exchange, API-key validation and subscribing already refused it. A product can still suspend or cancel its own subscription: that only
  takes access away.

## Alternatives considered

- **A free-text reason.** What an admin would expect to type. Not chosen, because of the audit-log rule above; a reason column on the
  organisation would hold it, but then it is not in the audit entry the ticket asks for.
- **Leave already-issued tokens to expire and document the window.** Cheaper (no read per request), but 15 minutes of an admin acting in a
  suspended organisation is the exact gap this ticket exists to close.
- **403 for a tenant-admin token of a suspended organisation.** Consistent with the exchange, but wrong for the console (above), and for any
  client, since only a new exchange explains the refusal.
- **Tell products with a webhook.** A product might want to hear that an organisation was suspended. Not part of this ticket, and nothing
  in the specification asks for it; it can be added without changing anything here.

## Consequences

- Each tenant-admin request now costs one small read of `tenants`.
- A reason that is none of the codes needs a new code (one line in `TenantStatusValidator.Reasons` and the console's list).
- A client that retries on 401 by signing in again gets a fresh identity token and then a `403 tenant_suspended` from the exchange; it does
  not loop.
- The built-in "Auth Service" product's token exchange for a suspended organisation was already refused, so its admins cannot open the console
  at all once their cached token ends; during the cached window they see the same "is suspended" screen.
