# 0008. Keep rate-limit counters in PostgreSQL, and fail closed

- Status: Accepted
- Date: 2026-11-03
- Tickets: AUTH-201 (PLT-04)

## Context

The limits on login, password reset, verification resend, service tokens and token exchange were counted in each process's memory. With
several instances behind a load balancer the real limit was the setting times the number of instances, and a restart cleared every count.
The limits exist to slow down guessing, so they must mean the same with one instance or ten.

The service already needs a PostgreSQL database and nothing else. A limit can be hit at most as often as the endpoint can be called, which
for these endpoints is a handful of requests a second per instance, not a hot path.

## Decision

- **Counters live in a table, `rate_limit_counters`, in the service's own database.** One atomic `INSERT ... ON CONFLICT DO UPDATE ...
  RETURNING` counts a use and restarts the count when the window has ended, so simultaneous uses from any instance are counted one by one.
  Time is the database's, so instances with different clocks agree on a window.
- **Windows are fixed**: a window starts at the first use and ends `window` later, as the built-in limiter's did. A refused use is still
  counted, which does not change who is refused.
- **The key is hashed** (SHA-256) before it is stored, so no IP address or person id is kept. A worker deletes counters a minute after
  their window ends (`Auth:RateLimit:CleanupSeconds`), so the table stays as small as the number of callers in the last windows.
- **The store is a setting**: `Auth:RateLimit:Store` = `Postgres` (the default) or `Memory`. `Memory` is the old per-process behaviour,
  for development, tests and a single instance.
- **No limit fails open.** If the store cannot be reached the request is refused with **503** `rate_limit_unavailable` and
  `Retry-After: 5`, and nothing else happens.

| Limit | If the store is unreachable | Why |
|---|---|---|
| Login (per IP) | Refused, 503 | An attacker who can make the store fail must not get unlimited guesses. |
| Password reset, forgot and reset (per IP) | Refused, 503 | Same, and a flood of reset emails is the other abuse it prevents. |
| Email verification resend (per IP) | Refused, 503 | Same: it sends email. |
| Service token (per IP) | Refused, 503 | Same: it checks a secret. |
| Token exchange (per person) | Refused, 503 | It reads the database for every call; unlimited, it is the way to starve it. |

Every one of these endpoints needs the same database the counters are in, so when the store is down the endpoint could not have done its
work anyway. Failing closed costs nothing real, and it is the only choice that cannot be turned into a bypass.

## Alternatives considered

- **Redis.** The usual choice, and faster. It would be a second piece of infrastructure to run, secure, back up and monitor, for limits
  that are not on a hot path. Not chosen; the `IRateLimitStore` interface is the seam if a deployment ever outgrows this.
- **Sticky sessions at the load balancer.** Makes the in-memory limit nearly right for one client, but is not a guarantee, and an attacker
  chooses their own address. Not chosen.
- **Fail open on a store outage.** Keeps login working when only the counters are broken. Not chosen: the counters are in the database that
  login needs, and open failure is exactly the state an attacker would try to cause.

## Consequences

- A new migration (`AddRateLimitCounters`) must run before the new version starts, as with every migration. Until it has, the limited
  endpoints answer 503.
- Each request to a limited endpoint costs one small write. Operators who run a single instance and want none can set `Memory`.
- A database outage now also stops the limited endpoints' counting, not just their work; the readiness check already takes the instance
  out of rotation for that.
- If the table ever becomes a bottleneck, `IRateLimitStore` can be implemented over Redis without touching the policies.
