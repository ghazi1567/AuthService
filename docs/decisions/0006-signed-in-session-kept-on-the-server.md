# 0006. Keep the signed-in session on the server

- Status: Accepted
- Date: 2026-11-02
- Tickets: AUTH-221 (UI01_T1), AUTH-195 (Web UI). Builds on [0002](0002-blazor-web-app-for-ui.md).

## Context

The API gives a person an access token (15 minutes) and a refresh token (30 days) when they sign in. The refresh token works once:
each refresh returns the next one, and using a spent one again ends the whole session. The web app needs to keep someone signed in
across page loads and long idle gaps, end the session on sign-out, and never leave a token or password where a browser script, a
log or a copy of the cookie can reach it (0002).

## Decision

- The browser gets one cookie, `auth_session` (`HttpOnly`, `SameSite=Lax`, `Secure` outside development). It holds an encrypted
  session id and the person's email, never a token. "Keep me signed in" makes it outlive the browser (30 days); otherwise it ends
  with the browser and the session lasts 12 hours.
- The tokens live in an `ISessionStore` on the server. The default `MemorySessionStore` keeps them in the process.
- Every request checks the session. When the access token has a minute or less left it is renewed, **one request at a time per
  session** (a lock), so two requests at once can never spend the same refresh token. If the API refuses (401 or 403) the session
  is removed and the person is sent to `/login?returnUrl=...&reason=expired`. If the API cannot be reached the person stays
  signed in and the renewal is tried again.
- Sign-out is a `POST /logout` form with an anti-forgery token. It removes the session here, tells the API so the refresh token
  stops working, and clears the cookie. It still works when the API is down.
- After signing in, only a path on this site is followed (`returnUrl`); anything else goes home.
- Nothing logs a password, token or session id: the session id is the cookie's secret, and the records that hold tokens print
  only safe fields.

## Alternatives considered

- **Tokens inside the cookie.** No server state, but the refresh token would sit in the browser, and two tabs refreshing at once
  would race on a token that works once.
- **Tokens in browser storage.** Rejected by 0002: any script on the page could read them.

## Consequences

- A restart of the web app signs everyone out, and **one copy of the web app only**: a second copy would not find the first one's
  sessions. Running several needs a shared `ISessionStore` (Redis or the database) and shared data-protection keys; the interface
  is there for it. Tracked as a follow-up when the web app gets a deployment image.
- The session's server-side life (12 hours, or 30 days when kept) is a choice made here, not read from the API, which only says when
  each token expires. Change it with `AuthService:Session:DefaultHours` and `RememberedDays`.
- Logging a person out of one browser does not end their other sessions.
