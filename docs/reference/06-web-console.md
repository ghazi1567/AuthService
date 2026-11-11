# The web console

> Part 6 of the Auth Service reference. Overview, glossary and symptom index: [README](README.md).

The web console is `src/AuthService.Web`, a Blazor Web App. It is the site people use to sign up, sign in, pick an organisation and a
product, run their organisation's admin console, and (for the people who run the platform) run the platform console. It is only a
client of the API: it has no database connection, and every decision about who may do what is made by the API. The web app decides only
what to show.

Three facts shape everything below.

- **Every page is server-rendered once per request.** No component sets a render mode (a search for `@rendermode` finds nothing). Program.cs
  registers `AddInteractiveServerComponents` and `AddInteractiveServerRenderMode`, and `App.razor` loads `blazor.web.js`, but no page
  is interactive. Forms are plain HTML posts handled by `EditForm` with `@formname`, or by minimal-API endpoints. This differs from
  [decision 0002](../decisions/0002-blazor-web-app-for-ui.md), which says the admin console uses interactive server rendering.
- **The browser never holds a token.** It holds one cookie with an encrypted session id. Tokens live in the web server's memory.
- **State lives in the web server's memory.** Sessions, console tokens and one-time secrets are in process memory. One copy of the web
  app only. A restart signs everyone out.

## Contents

- [How the console talks to the API](#how-the-console-talks-to-the-api)
- [The signed-in session](#the-signed-in-session)
- [Public pages: sign-up, sign-in, verify email, password reset, accept invitation](#public-pages-sign-up-sign-in-verify-email-password-reset-accept-invitation)
- [Organisation and product picker, and the hand-off into a product](#organisation-and-product-picker-and-the-hand-off-into-a-product)
- [The tenant console](#the-tenant-console)
- [The platform console](#the-platform-console)
- [Security headers, anti-forgery, safe redirects and error pages](#security-headers-anti-forgery-safe-redirects-and-error-pages)
- [Layouts, shared components and design tokens](#layouts-shared-components-and-design-tokens)
- [Symptom to where to look (this part)](#symptom-to-where-to-look-this-part)
- [Highest-risk areas in this part](#highest-risk-areas-in-this-part)
- [Unknowns and assumptions to confirm](#unknowns-and-assumptions-to-confirm)

---

## How the console talks to the API

### What it does

Every screen is a call, or several calls, to the Auth Service API made from the web server. The browser never calls the API. Nothing in
the web app talks to the database.

The rules the client enforces:

- **One client.** `AuthApiClient` (`src/AuthService.Web/Api/AuthApiClient.cs`) is the only code that makes HTTP calls to the API.
- **Errors are values, not exceptions.** Every call returns `ApiResult<T>` (`Api/ApiResult.cs`): a value, or an `ApiProblem`
  (`Api/ApiProblem.cs`). Pages decide what to show.
- **`ApiProblem` carries**: `Status`, `Code` (the API's stable error code, for example `rate_limited`), `Title`, `Detail`, per-field
  `Errors`, and `RetryAfter` (from the `Retry-After` header).
- **Two codes are made up by the web app**: `api_unreachable` (status 503; network failure, or the client's own timeout) and
  `unexpected_response` (the API answered with something that is not a problem document, an empty body, or a body the client could not
  read). A proxy's HTML error page therefore becomes `unexpected_response`, and its text is never shown.
- **Never follows redirects.** Program.cs sets `AllowAutoRedirect = false` on the primary handler. The API does not redirect.
- **Timeout.** `AuthService:Api:TimeoutSeconds`, default 15, allowed 1 to 120. A caller that cancels still gets the cancellation; only
  the client's own timeout becomes `api_unreachable`.
- **Holds no token.** Callers pass the person's token for each call. The bearer header is sent only when a token is given.
- **Names the real visitor.** `ClientAddressHandler` (`Api/ClientAddressHandler.cs`) removes any `X-Forwarded-For` on the outgoing
  request and sets it to the address the web app itself worked out (`HttpContext.Connection.RemoteIpAddress`, after the
  forwarded-headers middleware believed only its trusted proxies). An IPv4-mapped IPv6 address is sent as IPv4. Without this, every
  request would reach the API from the web app's own address and the API's per-address rate limits would be one shared budget.
  The API believes this header only from the web app's address (`Auth:Proxy` on the API).
- **Trusted proxies.** `AuthService:Proxy` (`Security/ProxyOptions.cs`). When any proxy or network is listed, Program.cs calls
  `UseForwardedHeaders` first, reading `X-Forwarded-For` and `X-Forwarded-Proto` only from listed proxies, with `ForwardLimit` entries
  believed from the right (default 1, allowed 1 to 10). With nothing listed the headers are never read. A bad entry stops the app
  starting (`InvalidOperationException` with the message naming the setting).
- **Lists are read whole.** `GetAllAsync<T>` asks for `limit=200` and follows the API's `next` cursor (whatever properties it holds,
  handed back as query parameters), up to 50 pages. A failing page fails the whole read, so a screen never shows half a list as if it
  were all of it.
- **The audit log and webhook list are not read whole.** Those pages use `GetAsync` with `limit` of 50 to 200 and a "Load more" link
  (see [The tenant console](#the-tenant-console)).

### How it is implemented

Entry points (methods on `AuthApiClient`; the API route is chosen by the caller):

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `GET /` (API root) | none | n/a | `AuthApiClient.PingAsync` | Reads the API's root text. Used only by tests. |
| `GET {path}` | optional bearer | n/a | `GetAsync<T>` | Reads one JSON resource. |
| `GET {path}?limit=200[&cursor...]` | optional bearer | n/a | `GetAllAsync<T>` | Reads a whole list, page by page. |
| `POST {path}` | optional bearer | n/a | `PostAsync<TRequest,TResponse>` | JSON in, JSON out. |
| `POST {path}` | optional bearer | n/a | `PostNoContentAsync<TRequest>` | JSON in, no body out. |
| `PUT {path}` | bearer | n/a | `PutNoContentAsync<TRequest>` | JSON in, no body out. |
| `PATCH {path}` | bearer | n/a | `PatchAsync<TRequest,TResponse>`, `PatchNoContentAsync<TRequest>` | Changes a resource. |
| `DELETE {path}` | bearer | n/a | `DeleteAsync` | Deletes a resource. |

Flow of one call (`SendAsync`):

1. The caller builds the request. `ClientAddressHandler` (a `DelegatingHandler`) replaces `X-Forwarded-For`.
2. `httpClient.SendAsync`. `HttpRequestException` becomes `ApiProblem.FromUnreachable()` (503, `api_unreachable`). A
   `TaskCanceledException` while the caller has not cancelled (the client timeout) becomes the same.
3. On a success status the body is read. `InvalidOperationException`, `JsonException` or `NotSupportedException` while reading becomes
   `ApiProblem(status, "unexpected_response")`.
4. On an error status `ApiProblem.Parse` reads `code`, `title`, `detail` and `errors` from the JSON, and `Retry-After` (as a delta). An
   empty or non-JSON body gives `unexpected_response` with the status.

Startup wiring is in `src/AuthService.Web/Program.cs`: `AddHttpClient<AuthApiClient>` with `BaseAddress` and `Timeout` from
`AuthApiOptions`, `ClientAddressHandler`, and `AllowAutoRedirect = false`. `AuthApiOptionsValidator` runs `ValidateOnStart`, so a
missing or malformed `AuthService:Api:BaseUrl` stops the app (`AuthService:Api:BaseUrl is required: the address of the Auth Service
API.`, or `... must be an absolute http or https address.`).

- Data: none. The web app has no tables.
- Libraries: ASP.NET Core `HttpClient` factory, `System.Text.Json`, ASP.NET Core forwarded-headers middleware.
- Background workers: none.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `AuthService:Api:BaseUrl` / `AuthService__Api__BaseUrl` | none. **Required.** Development: `http://localhost:5080`. Compose: `http://api:8080` | `Api/AuthApiOptions.cs`; `appsettings.Development.json`; `deploy/docker-compose.yml` | Missing or not an absolute http/https address: the app refuses to start. Wrong host: every page shows its "isn't responding" wording. |
| `AuthService:Api:TimeoutSeconds` / `AuthService__Api__TimeoutSeconds` | 15 (allowed 1 to 120) | `Api/AuthApiOptions.cs` | A slow API becomes `api_unreachable` after this long. Lists of many pages multiply the wait. |
| `AuthService:Proxy:TrustedProxies` / `AuthService__Proxy__TrustedProxies__0` | empty | `Security/ProxyOptions.cs` | Exact addresses of proxies to believe. |
| `AuthService:Proxy:TrustedNetworks` / `AuthService__Proxy__TrustedNetworks__0` | empty. Compose sets it to `${DOCKER_SUBNET:-172.29.200.0/24}` | `Security/ProxyOptions.cs`; `deploy/docker-compose.yml` | CIDR ranges to believe. A bare address (`10.0.0.5`) is refused. If the proxy is not covered, every visitor looks like the proxy to the API. |
| `AuthService:Proxy:ForwardLimit` / `AuthService__Proxy__ForwardLimit` | 1 (allowed 1 to 10) | `Security/ProxyOptions.cs` | How many proxies are in front of the app. |
| Blank entries in the proxy lists | ignored | `ProxyOptions.Proxies`, `Networks` | Hosting platforms often pass an empty variable. |

### Failure modes and production debugging

- **What the visitor sees.** The API being down, slow or answering garbage reaches the page as `api_unreachable` or
  `unexpected_response`. Public forms show "Auth Service isn't responding right now" (their fallback for every code they do not know,
  not only an outage). List pages show `ErrorState` ("We couldn't load ...") with a `Reference` code.
- **What is logged.** **Nothing.** `AuthApiClient.SendAsync` catches `HttpRequestException` and the timeout without logging. The
  web app's only log lines are three in `SessionService` (see [The signed-in session](#the-signed-in-session)). To see a failed call,
  look in the API's logs and at the proxy.
- **The `Reference` code.** `ErrorState` and the pages show the last 8 characters of `HttpContext.TraceIdentifier`, lower-cased.
  **Assumption (confirm):** under Kestrel `TraceIdentifier` is `<connection id>:<request number in hex>`, so these 8 characters are the
  request's sequence number on its connection, not a unique id, and the web app does not log it. Tests only check the shape
  (`[a-z0-9]{8}`). `Pages/Error.razor` uses `Activity.Current?.Id ?? TraceIdentifier` instead, which is meaningful.
- **Everyone is rate limited at once.** The API sees one address. Check both sides: the web app's `AuthService:Proxy` must cover the
  reverse proxy, and the API's `Auth:Proxy` must cover the web app (compose sets both to `DOCKER_SUBNET`). See
  [deployment.md](../deployment.md), "If something does not work".
- **A list is cut off.** `GetAllAsync` stops after 50 pages (10,000 rows at 200 a page) and returns what it has as a success. No
  organisation is expected to have that many rows, but nothing tells the person if one does.
- **Slow pages.** A page can make several calls in series (for example `Invitations` reads invitations, subscriptions, roles and
  members). Each has its own timeout of up to `TimeoutSeconds`.
- Confirming the API address from the web container: `curl http://api:8080/health/ready` from inside the stack, or the
  `curl https://auth.example.com/health/ready` check in the deployment guide. `GET /healthz` on the web app says only that the web app is
  running (`ok`).

### Decisions and open questions

- Errors as values, with the page choosing the wording, keeps raw exception text and proxy error pages away from visitors. The cost is
  that the web app records nothing about a failed call. **Open question:** whether the web app should log the status and code of a
  failed API call (without the path's ids or any token) so on-call can see them.
- The client never retries. A transient failure is shown to the person, who retries by hand.
- Page size 200 matches the API's maximum (`Paging.MaxLimit = 200` in `src/AuthService.Api/Common/Paging.cs`). If the API's maximum
  changes, `GetAllAsync` must change with it, or the API will answer 400 to every list.
- **Unknown:** whether the API logs or uses the incoming W3C `traceparent` header, which would let an engineer join a web request to an
  API request. Settle it by reading the API's logging setup in `src/AuthService.Api/Observability`.

---

## The signed-in session

### What it does

After a person signs in, the console remembers them without ever giving the browser a token. The browser holds one cookie
(`auth_session`) that contains an encrypted session id, the person's id and their email. The tokens (an access token and a refresh
token from the API) stay in the web server's memory, keyed by the session id. The web app renews the access token shortly before it
runs out and ends the session when the API will not renew it.

Rules:

- **Session length.** "Keep me signed in": `RememberedDays` = 30 days (allowed 1 to 30; the API's refresh token also lasts 30 days).
  Otherwise `DefaultHours` = 12 hours (allowed 1 to 720), and the cookie ends when the browser closes.
- **Renewal.** The access token is renewed when it has `RefreshBeforeSeconds` = 60 seconds or fewer left (allowed 0 to 600). The API's
  access token lasts 15 minutes (`Auth:Tokens:AccessTokenMinutes`).
- **One renewal at a time per session.** A refresh token works once and the API ends the whole session if one is used twice, so
  `ISessionStore.LockAsync` makes concurrent requests wait and reuse the result.
- **API refuses (401 or 403): the session ends** and the person is sent to sign in. **API unreachable or failing (anything else): the
  person stays signed in** and renewal is tried on the next request.
- **Sign-out** is `POST /logout` with an anti-forgery token. The session is removed here first, then the API is told so the refresh
  token stops working. It still works when the API is down.
- **The cookie.** `HttpOnly`, `SameSite=Lax` (so a link from an email still arrives signed in), `Secure` always outside Development
  (`SameAsRequest` in Development), no sliding expiry, login path `/login`, return parameter `returnUrl`.
- **Nothing logs a password, token or session id** (tested by `NoPasswordTokenOrSessionIdIsEverLogged`).

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /login` (form `login`) | none | `Components/Pages/Login.razor` `SignInAsync` | `SessionService.StartAsync`, `HttpContext.SignInAsync` extension | Calls the API, starts the session, sets the cookie. |
| `POST /logout` | cookie (checked by hand; `AllowAnonymous`) | `SessionAuthentication.MapSessionEndpoints` | `SessionService.EndAsync` | Validates the anti-forgery token, ends the session, clears the cookie, redirects. |
| every request with the cookie | cookie | `CookieAuthenticationEvents.OnValidatePrincipal` | `SessionService.EnsureFreshAsync` | Checks the session exists and is in date, renews tokens if needed. |
| any protected page without a valid cookie | n/a | `CookieAuthenticationEvents.OnRedirectToLogin`; `RedirectToLogin.razor` | n/a | Redirects to `/login?returnUrl=...`, adding `reason=expired` when a session cookie was sent. |
| `GET /account` | `[Authorize]` | `Components/Pages/Account.razor` | n/a | A small page that shows the email and a sign-out button. |

Flow of a sign-in (`Login.razor`):

1. `POST /api/auth/login` through `AuthApiClient.PostAsync<LoginRequest, LoginResponse>`.
2. `SessionService.StartAsync` reads the `sub` claim from the access token without checking its signature
   (`Security/AccessTokenClaims.ReadUserId`; the API checks signatures on every call). No usable `sub` returns null and the page shows
   "We couldn't sign you in".
3. It creates a `UserSession` (`Security/UserSession.cs`): a random 32-byte id (base64url), user id, email, access token, access token
   expiry (now plus `expires_in`), refresh token, and `ExpiresAt` (now plus `RememberedDays` or `DefaultHours`). `store.AddAsync` saves it.
4. `HttpContext.SignInAsync(session, remember)` builds the cookie with claims `NameIdentifier` (user id), `Name` (email) and `sid`
   (session id). `IsPersistent` is `remember`; the ticket expires at `session.ExpiresAt`.
5. `Navigation.NavigateTo(LocalRedirect.Safe(ReturnUrl))`.

Flow of a request with the cookie:

1. `OnValidatePrincipal` reads the `sid` claim and calls `SessionService.EnsureFreshAsync`.
2. No session in the store: `SessionState.Ended`. Session past `ExpiresAt`: removed, `Ended`.
3. Token fresh: `Active`. Otherwise take the per-session lock, read the session again (another request may have renewed it), then
   `POST /api/auth/refresh` with the refresh token.
4. Success: save the new access token, expiry and refresh token. 401 or 403: log, remove the session, `Ended`. Anything else: log a
   warning, `Active`.
5. `Ended` makes the handler call `RejectPrincipal` and `SignOutAsync`, so the page's `[Authorize]` challenge redirects to login.

Other parts:

- `ISessionStore` (`Security/ISessionStore.cs`): `AddAsync`, `FindAsync`, `SaveAsync`, `RemoveAsync`, `LockAsync`. The only
  implementation is `MemorySessionStore`: a `ConcurrentDictionary` with a `SemaphoreSlim` per session. It purges expired sessions only
  when a new session is added.
- `SessionService.GetAccessTokenAsync(sessionId)` is what pages call to get the person's current identity token (renewed first).
- Data: none in a database. In memory: `MemorySessionStore`, `ConsoleTokenCache`, `OneTimeSecrets`, all registered as singletons in
  `SessionAuthentication.AddSessionAuthentication`.
- Data protection keys protect the cookie (and the anti-forgery tokens and the pending-invitation cookie). With
  `AuthService:DataProtection:KeysPath` set, keys are written to that folder (`PersistKeysToFileSystem`, application name
  `AuthService.Web`). Without it they are in memory only.
- Libraries: ASP.NET Core cookie authentication and data protection.
- Background workers: none. Expired sessions are removed lazily.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `AuthService:Session:CookieName` / `AuthService__Session__CookieName` | `auth_session` | `Security/SessionOptions.cs` | Required, not empty. Also what `OnRedirectToLogin` looks for to decide whether to add `reason=expired`. |
| `AuthService:Session:RememberedDays` / `AuthService__Session__RememberedDays` | 30 (1 to 30) | `Security/SessionOptions.cs` | Length of a "Keep me signed in" session. Capped at 30 to match the API's refresh token lifetime. |
| `AuthService:Session:DefaultHours` / `AuthService__Session__DefaultHours` | 12 (1 to 720) | `Security/SessionOptions.cs` | Length of an ordinary session. |
| `AuthService:Session:RefreshBeforeSeconds` / `AuthService__Session__RefreshBeforeSeconds` | 60 (0 to 600) | `Security/SessionOptions.cs` | How early the access token is renewed. |
| `AuthService:DataProtection:KeysPath` / `AuthService__DataProtection__KeysPath` | unset. Container: `/keys` (volume `webkeys`) | `Program.cs`; `Dockerfile` (`mkdir /keys`, owned by the app user); `deploy/docker-compose.yml` | Unset: keys are in memory, so every restart makes all cookies and open forms unreadable. Set but not writable: the app fails when it first uses the keys. |
| `ASPNETCORE_ENVIRONMENT` | `Production` in the image | `Dockerfile` | `Development` makes the cookie `SameAsRequest`, skips the error handler and the `Secure` flag on the invitation cookie. |
| `Logging__LogLevel__Microsoft.AspNetCore.DataProtection` | `Error` in compose | `deploy/docker-compose.yml` | Hides the warning that keys are stored unencrypted. |
| `Auth:Tokens:AccessTokenMinutes` / `RefreshTokenDays` (API) | 15 / 30 | `src/AuthService.Api/Tokens/IdentityTokenOptions.cs` | Decide how often the web app renews and how long a session can really last. |

Invalid session settings stop the app at start (`ValidateDataAnnotations().ValidateOnStart()`).

### Failure modes and production debugging

- **What the person sees.**
  - Session gone or refused: `/login?returnUrl=<page>&reason=expired`, and the sign-in page shows "Your session ended".
    `reason=expired` is added whenever a cookie named `CookieName` was sent, even one that could not be decrypted.
  - A form post that fails its anti-forgery check: an empty HTTP 400 (see [Security headers, anti-forgery ...](#security-headers-anti-forgery-safe-redirects-and-error-pages)).
- **What is logged** (the web app's only log lines, in `SessionService`):
  - `A session of user {UserId} ended: the API would not renew it ({Code}).` (Information)
  - `Could not renew a session of user {UserId} now ({Code}); it will be tried again.` (Warning)
  - `A session of user {UserId} ended here, but the API could not be told ({Code}).` (Warning)
- **Audit actions.** None recorded by the web app. The API records `auth.login.succeeded` and, when a used refresh token is presented
  again, `auth.refresh_token.reuse_detected`.
- **What a restart does.** The session store, the console token cache and the one-time secrets are emptied. Every cookie still arrives.
  If the keys survived, it decrypts, finds no session, and the person goes to sign in with "Your session ended". If the keys did not
  survive, the cookie cannot be read and the effect is the same. Open forms fail their anti-forgery check either way if the keys changed.
- **Edge cases.**
  - Two copies of the web app: the second does not find the first's sessions, and signing in on one then using the other fails. Not
    supported (decision 0006).
  - Sign-out ends only this browser's session; others stay.
  - `ConsoleTokenCache` entries are not removed on sign-out. They are keyed by session id, which no longer works, and expire by themselves.
  - `UserSession.ToString()` prints the session id (`Id = ...`) and nothing else; it does not print tokens.
- **Likely production causes.**
  - *Everyone signed out after a deploy.* Expected, because the store is in memory. If open forms also fail (HTTP 400), the keys were
    lost: check that the `webkeys` volume is mounted at `/keys` and `AuthService__DataProtection__KeysPath` is set. Confirm with
    `docker compose exec web ls -l /keys` (it should hold `key-*.xml`).
  - *Sign-in works but the next page asks to sign in again.* The cookie was not stored. It is `Secure` outside Development, so a
    browser using plain `http://` to the site drops it. Check the browser's `Set-Cookie` handling and that the site is served over HTTPS.
  - *The API refuses renewal for everyone.* Look for the Information line above with a `{Code}`; on the API side the refresh token family
    is revoked (table `refresh_tokens`, column `revoked_at`). Read-only check:
    `select family_id, count(*), max(revoked_at) from refresh_tokens where user_id = '<id>' group by family_id order by 3 desc;`
  - *The API's token is rejected while the web app still thinks it is fresh* (clock difference between the web server and the API, or a
    signing-key rotation). See the loop described under Decisions below.
  - *Platform admin rotated the signing key.* The page warns that every token stops working and "including you".

### Decisions and open questions

- **Server-side session instead of tokens in the cookie** (decision 0006): no token in the browser, and no race between two tabs
  refreshing one single-use token. The cost is memory state and one instance.
- **Lax, not Strict**, so links from emails and other products arrive signed in.
- **The session length is chosen by the web app**, not read from the API (decision 0006).
- **The `sub` claim is read without verifying the signature.** The web app received the token directly from the API over its own
  connection, and the API verifies it on every later call.
- **Possible redirect loop. Assumption (confirm):** `Login.razor` sends an already signed-in visitor straight on to `returnUrl`. A
  console page that gets 401 from the API for the identity token (`ConsoleSession` and `ChooseOrganisation` treat 401 on
  `GET /api/auth/users/me` as "session ended") redirects to `/login?...&reason=expired`. If the web session is still alive (its token
  is not yet within `RefreshBeforeSeconds` of expiry by the web server's own clock), `Login` redirects straight back, and the browser
  bounces until the token reaches the renewal window or the browser stops it. Triggers could be a clock difference between the web
  server and the API, or an emergency signing-key rotation. I did not run this; the tests cover the 401 redirect but not the loop.
  Settle it by reproducing with a fake API that answers 401 to `users/me` while the session is fresh.
- **Inconsistency:** the comment in `SessionService` and decision 0006 say the session id is "the cookie's secret" and never logged, yet
  `UserSession.PrintMembers` prints it. It is not logged anywhere today, and the cookie itself is encrypted, so the id alone cannot forge
  a cookie. The comment and the code disagree.
- **Unfinished:** `ISessionStore` exists for a shared store (Redis or a database) but none is written. [deployment.md](../deployment.md)
  lists "Sessions that survive a restart" under what the setup does not give.
- The keys in the `webkeys` volume are stored unencrypted. The compose file explains why that is accepted (they protect an opaque id,
  never a token) and hides the warning.

---

## Public pages: sign-up, sign-in, verify email, password reset, accept invitation

### What it does

These are the pages a person uses before they are signed in. All use the `AuthLayout` shell and are rendered on the server.

- **Sign-up (`/signup`).** A new person gives their full name, organisation name, work email and password. The API creates the
  organisation and the account and emails a verification link. The page then says "Check your email" (the link works for 24 hours,
  page text) with a resend button.
- **Sign-in (`/login`).** Email, password, and "Keep me signed in".
- **Verify email (`/verify-email`).** The page the verification email opens.
- **Forgot and reset password (`/forgot-password`, `/reset-password`).** Ask for a link (works for 1 hour, page text), then choose a new
  password. A reset signs the person out everywhere else (page text).
- **Accept invitation (`/accept-invitation`).** A new person chooses a password and joins. A person who already has an account accepts
  while signed in.

Rules that appear on every one of them:

- **Never say whether an address has an account.** Sign-up treats `email_already_registered` as success and shows the same "Check your
  email" screen. Forgot-password shows one "Check your email" screen whenever the API accepts the request, and reads nothing about the
  address from the answer. Resend verification shows one neutral message. (Any other API error on these forms is shown as a failure,
  not as success.)
- **Passwords are never sent back into a page.** After each post the page sets the password properties to null. Password fields start
  empty and say whether they hold an old (`current-password`) or new (`new-password`) password.
- **A password has 8 to 128 characters** (`PasswordPolicy.MinLength` and `MaxLength` in `Api/SignupContracts.cs`). The page checks it
  and the API checks it again; the API's reasons are shown beside the field.
- **A rate limit shows a live countdown** from the API's `Retry-After` (`rate_limited` code). Without a `Retry-After`, "Wait a moment".
- **Link tokens never stay in the address bar, a log, a referrer or a cache.** See below.
- **Wording per API answer.** Any code a page does not know is shown as "Auth Service isn't responding right now".

### How it is implemented

Pages and what each posts to the API:

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /signup` (form `signup`) | none | `Pages/Signup.razor` `SignUpAsync` | `POST /api/auth/signup` | Creates organisation and account. Body `SignupRequest(TenantName, Email, Password, DisplayName)`. |
| `POST /signup` (form `resend`) | none | `Signup.razor` `ResendAsync` | `POST /api/auth/email/verify/resend` | Resends the verification email. |
| `POST /login` | none | `Pages/Login.razor` `SignInAsync` | `POST /api/auth/login` | Signs in (see the session section). |
| `GET /verify-email?token=...` | none | `Pages/VerifyEmail.razor` | n/a | Shows "Checking your link" with a form holding the token. Does not call the API. |
| `POST /verify-email` (form `verify`) | none | `VerifyEmail.razor` `VerifyAsync` | `POST /api/auth/email/verify` | Uses the link. |
| `POST /verify-email` (form `resend`, `again`) | none | `VerifyEmail.razor` `ResendAsync`, `AgainAsync` | `POST /api/auth/email/verify/resend` | Asks for a new link. |
| `GET /forgot-password`, `POST` (forms `forgot`, `again`) | none | `Pages/ForgotPassword.razor` | `POST /api/auth/password/forgot` | Asks for a reset link. |
| `GET /reset-password?token=...` | none | `Pages/ResetPassword.razor` | n/a | Shows the new-password form at once. |
| `POST /reset-password` (form `reset`) | none | `ResetPassword.razor` `ResetAsync` | `POST /api/auth/password/reset` | Body `ResetPasswordRequest(Token, NewPassword)`. |
| `GET /accept-invitation?token=...` | none | `Pages/AcceptInvitation.razor` | n/a | Chooses a view (see below). |
| `POST /accept-invitation` (forms `accept-new`, `accept-existing`) | none, or the session for `accept-existing` | `AcceptInvitation.razor` `AcceptNewAsync`, `AcceptExistingAsync` | `POST /api/auth/invitations/accept` | Body `AcceptInvitationRequest(Token, Password)`. `accept-existing` sends the person's access token and no password. |
| `GET /` | none | `Pages/Home.razor` | n/a | Signed-in visitors go to `/choose-organisation`. Others see a placeholder page with a sign-in link. |
| `GET /account` | `[Authorize]` | `Pages/Account.razor` | n/a | Shows the email and a sign-out button. |

What each page shows for each API answer:

| Page | API answer (status, `code`) | What the person sees |
|---|---|---|
| Sign-up | success, or `email_already_registered` | "Check your email" with the address. |
| Sign-up | 400 with `errors` for `DisplayName`, `TenantName`, `Email` or `Password` | The first reason beside that field. Other field names are ignored. |
| Sign-up | `rate_limited` | "Too many attempts" with the wait. |
| Sign-up | anything else | "We couldn't create your account ... isn't responding". |
| Sign-in | success | Session starts; redirect to `returnUrl` (a local path) or `/`. |
| Sign-in | `invalid_credentials` | "The email or password is incorrect" with a reset link. |
| Sign-in | `email_not_verified` | "Verify your email first". |
| Sign-in | `account_disabled` | "This account is turned off". |
| Sign-in | `rate_limited` | "Too many attempts" with a countdown on the button. |
| Sign-in | anything else, or a token with no usable `sub` | "We couldn't sign you in". |
| Verify email | success | Redirect to `/verify-email?result=verified`. |
| Verify email | `verification_link_invalid` | Redirect to `/verify-email?result=invalid`: "This link can't be used" with an email box. |
| Verify email | anything else | The link stays on the page with a "Try again" button; "Your link is still good". No automatic retry. |
| Forgot password | success | "Check your email" (the same for any address). |
| Forgot password | `rate_limited`, or anything else | Wait message, or "We couldn't send the link". |
| Reset password | success | Redirect to `/reset-password?result=done`. |
| Reset password | `reset_link_invalid` | Redirect to `/reset-password?result=invalid`. |
| Reset password | 400 with `errors.NewPassword` | The reason beside the field. The link is still usable, because the API checks the password before it touches the link. |
| Accept invitation | success | Redirect to `/accept-invitation?result=done`: "You've joined the team" and a Continue link to `/`. |
| Accept invitation | `invitation_invalid` | Redirect to `?result=invalid`: "This invitation can't be used". |
| Accept invitation | `login_required` | "Sign in to accept" (the email already has an account). |
| Accept invitation | `wrong_account` | "This invitation is for a different account" with "Sign out and switch account". |
| Accept invitation | 400 with `errors.Password`, signed in | The page switches to the "choose a password" form (the email has no account yet). |
| Accept invitation | 400 with `errors.Password`, signed out | The reason beside the password field. |

**How link tokens are handled.**

1. The emailed link is `/verify-email?token=...`, `/reset-password?token=...` or `/accept-invitation?token=...` (built by the API
   from `Email:PublicBaseUrl`).
2. The page reads the token from the query (`SupplyParameterFromQuery`) and writes it into one hidden form field.
3. `wwwroot/app.js` `startLinkForms` finds `form[data-link-form]`, calls `history.replaceState(null, "", location.pathname)` to remove
   the token from the address bar and the history entry, and for `data-link-form="auto"` (verify email) submits the form with
   `requestSubmit()`. For `"clean"` (reset password, accept invitation as a new person) it waits for the person.
4. The form posts to a clean address (`action="/verify-email"` and so on) with an anti-forgery token.
5. **Verify email uses a POST, not a GET.** An email scanner that only fetches the link cannot use it up. Without script the person
   presses "Verify my email".
6. Every response sends `Referrer-Policy: no-referrer` and `Cache-Control: no-store` (see the headers section), and tokens are never
   logged (`LinkTokenSecurityTests`).
7. If someone reloads after the script removed the token, the page has no token and shows "This link can't be used".

**Accept invitation views** (`AcceptInvitation.razor` `OnInitialized`): the token comes from a posted form, else the query, else the
`invitation_pending` cookie.

- No token: "This invitation can't be used".
- Token and signed out: `Choose` (password form for a new person).
- Token and signed in: `Existing` ("You're signed in as ... Accept the invitation with this account?"), with "Use a different account".
- After `login_required`: `LoginNeeded`, with a Sign in link to `/login?returnUrl=%2Faccept-invitation`. The token is not in that link.

**The pending-invitation cookie** (`Security/PendingInvitation.cs`). A person whose email already has an account must sign in and come
back. The token waits in the cookie `invitation_pending`: encrypted and time-limited with a data protection protector (purpose
`AuthService.Web.PendingInvitation`), `HttpOnly`, `SameSite=Lax`, `Secure` outside Development, path `/`, valid for 1 hour. It is
stored whenever the page holds a token, read when the page opens without one, and cleared (`Finish`) when the invitation is used or
reported invalid. A tampered or expired cookie reads as "no token".

**`app.js`** (`wwwroot/app.js`) is progressive enhancement only; every page works without it, except that the link-token clean-up and
the auto-submit happen in the script. It adds: show/hide password, a busy label on a submitted form (`data-busy-label`), the
rate-limit countdown (`data-countdown`), password-rule ticks (`data-rules-for`), the link-token clean-up (`data-link-form`), an
unsaved-changes warning (`data-unsaved-guard`), copy-to-clipboard (`data-copy-from`), and auto-submit of `form[data-autosubmit]`. It
re-runs on Blazor's `enhancedload` event.

- Data: none in the web app. The API's tables involved: `users`, `tenants`, `tenant_memberships`, `email_verification_tokens`,
  `password_reset_tokens`, `invitations`, `email_outbox`.
- Background workers: the API's email worker must be running, or no link arrives. See the notifications part of this reference.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Email:PublicBaseUrl` / `Email__PublicBaseUrl` (API) | none; compose: `https://${APP_HOST}` | `deploy/docker-compose.yml` | Where links in emails point. It must be this web console, which serves `/verify-email`, `/reset-password` and `/accept-invitation`. |
| `AuthService:Session:*` | see the session section | `Security/SessionOptions.cs` | Used by sign-in. |
| Fixed in code: password 8 to 128 | `PasswordPolicy` | `Api/SignupContracts.cs` | Must match the API's own rule, or the page and API disagree. |
| Fixed in code: resend button waits 60 seconds | `data-countdown` default | `Signup.razor`, `VerifyEmail.razor`, `ForgotPassword.razor` | Mirrors the API's resend limit; the API is the authority. |
| Fixed in code: invitation cookie 1 hour | `PendingInvitation.Lifetime` | `Security/PendingInvitation.cs` | A person who takes longer than an hour to sign in loses the waiting link. |

### Failure modes and production debugging

- **What the caller sees** is in the table above. Status codes of the web app itself: the pages answer 200 (the post is handled and the
  page re-rendered, or redirected). A failed anti-forgery check answers 400.
- **What is logged.** No page logs anything. Search the API's logs and audit log. Audit actions the API records for these flows:
  `auth.signup.completed`, `auth.tenant.created`, `auth.login.succeeded`, `auth.login.failed`, `auth.account.locked`,
  `auth.email.verified`, `auth.password_reset.completed`, `auth.invitation.accepted`.
- **Edge cases and half-states.**
  - Verify email with the API down: the link stays on the page and the button reads "Try again". The link is not consumed.
  - Reset password has no "check the link" step. The form shows at once; the link is used only when the password is sent.
  - A signed-in person opening an invitation link sees the `Existing` view, which has no `data-link-form`, so `?token=` **stays in the
    address bar and history** until they post. The other link pages remove it.
  - Signing in as a different person from the one invited gives "wrong account". The "Sign out and switch account" button posts to
    `/logout` with `returnUrl=/accept-invitation`, and the waiting cookie brings the token back.
- **Likely production causes.**
  - *No email arrives.* Not a web problem: check `email_outbox` and the email worker (notifications part). Read-only SQL:
    `select status, count(*), min(created_at) from email_outbox group by status;`
  - *The link opens the wrong site or a 404.* `Email__PublicBaseUrl` on the API is wrong or missing the web console's address.
  - *"This link can't be used" for a fresh link.* The link was already used, expired (24 hours verification, 1 hour reset, per page
    text; the invitation lifetime is the API's), or the person reloaded after `app.js` removed the token.
  - *Everyone sees "Too many attempts" at sign-in.* The API sees one address; see the proxy trust rows above.
  - *"We couldn't sign you in" for everyone.* The API is unreachable from the web container (`AuthService__Api__BaseUrl`), or it answers
    with a code the page does not know (for example a 5xx via the proxy).
  - *Sign-in succeeds on the API but the person is sent back to the sign-in page.* The session cookie is not being kept; see the session
    section.
  - *A person says they were asked to sign in again at the invitation page and it then said "can't be used".* The one-hour
    `invitation_pending` cookie expired, or its data protection keys changed (restart without the `webkeys` volume).
- Confirm a user's verification state read-only: `select email, email_verified_at, failed_login_count from users where email = '<address>';`

### Decisions and open questions

- **A POST to use an emailed link** (verify email) stops scanners using it up. Reset password and invitation links do not need this
  because they need a password typed anyway.
- **One neutral answer for any address** trades a little friendliness for not leaking which addresses have accounts.
- **Inconsistent:** the `Existing` view of the invitation page leaves the token in the address bar (above). **Open question:** whether
  that is deliberate, because the person is signed in and the token is also in the pending cookie.
- `Pages/Home.razor` still says "The web console is being built. The tenant console and the platform console arrive with the design".
  That text is shown to every signed-out visitor at `/`. It is out of date.
- The README's web console section is partly out of date (it says the other auth screens, the consoles, the picker and the container
  image are "not yet" built, that the tenant and platform shells are "still empty", and that accept-invitation success "leads to `/`
  until the organisation picker exists"). All of those exist in the code. Trust the code.
- **Assumption (confirm):** the 24-hour and 1-hour link lifetimes quoted on the pages match the API. They are text in the Razor files,
  not read from the API. Settle by reading the API's verification and reset token lifetimes.
- The `Fields` lists in `Signup.razor` and the `errors` key names (`NewPassword`, `Password`, `TenantName`) must match the API's
  validation field names exactly, or the reasons are silently dropped and the person sees only the fallback.

---

## Organisation and product picker, and the hand-off into a product

### What it does

After sign-in a person picks the organisation they want, then the product they want to open. A product is a separate system; opening
it signs the person in there by handing it a token for that one organisation and product.

Rules:

- **Organisations (`/choose-organisation`).** Lists the person's organisations from `GET /api/auth/users/me`, sorted by name. Active
  ones link to the product picker and have a "Manage" link to the admin console. Invited ones show "Open the link in your invitation
  email to join." Disabled ones show "Your access is turned off. Ask an admin." A person in exactly one organisation, which is active,
  is sent straight on (unless they arrived with `?change=1`, or the request is not a GET). A person in none sees the create form open.
  "Create another organisation" posts `POST /api/auth/tenants`.
- **Choosing an organisation drops every console token** for the session (`ConsoleSession.ForgetTokens`), so the next console visit
  trades for a fresh one.
- **Products (`/choose-product?tenantId=...`).** The person's products in that organisation come from
  `GET /api/auth/users/me/applications?tenantId=...`. Each has a state: `Available`, `NoAccess`, `NotSubscribed`,
  `SubscriptionSuspended`, `SubscriptionCancelled` (`Api/PickerContracts.cs` `ProductState`). Only `Available` with a non-empty
  `LaunchUrl` can be opened; the others are greyed out with a reason. A suspended organisation (403 `tenant_suspended`) is named as such.
- **Straight through.** If exactly one product can be opened, the person has no admin console, there is no error in the address and
  the address does not say `stay=1`, the page submits the open form by itself (`data-autosubmit`).
- **Admin entry.** Someone who can read any part of the console (members, roles, product access, API keys, subscriptions or audit, in
  that order) also sees "Admin console" and a list of connectable products (`GET /api/auth/applications`).
- **Opening a product** is a form post of a freshly exchanged product token to the product's launch address (decision 0007). The launch
  address comes from the API's own answer, never from the request. The token is in the body of the post, never in an address.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `GET /choose-organisation` | `[Authorize]` | `Pages/ChooseOrganisation.razor` | `GET /api/auth/users/me` | Lists organisations; forwards if there is one. |
| `POST /choose-organisation` (form `create`) | `[Authorize]` | `ChooseOrganisation.razor` `CreateAsync` | `POST /api/auth/tenants` | Creates an organisation owned by the person; goes to its product picker. |
| `GET /choose-product?tenantId=` | `[Authorize]` | `Pages/ChooseProduct.razor` `LoadAsync` | `users/me`, `users/me/applications`, `ConsoleSession.LoadAsync`, `GET /api/auth/applications` | Lists products and the admin entry. |
| `POST /choose-product/open` | session (checked by hand; `AllowAnonymous`) | `Security/ProductEndpoints.cs` `OpenAsync` | `POST /api/auth/token/exchange` | Hands the token to the product. |

Flow of `POST /choose-product/open`:

1. Validate the anti-forgery token (failure: HTTP 400, empty). The form must carry `tenantId` and `applicationId` as GUIDs, else 400.
2. Not signed in: redirect to `/login?returnUrl=<picker>`. No usable access token (`SessionService.GetAccessTokenAsync` null): the same
   with `&reason=expired`.
3. Read the person's products again with `GetAllAsync` on `users/me/applications?tenantId=`. A failure redirects to the picker with
   `error=<code>`.
4. Find the product. It must be `Available` with an absolute `LaunchUrl`; otherwise redirect with `error=not_available`. Nothing is
   exchanged.
5. `POST /api/auth/token/exchange` with `TokenExchangeRequest(tenantId, applicationId)` and the person's identity token. Nothing is
   cached for products. A failure redirects to the picker with `error=` and, for 429, `wait=<seconds>` (clamped 1 to 3600, default 60).
6. `SecurityHeaders.AllowFormPostTo(context, address)` stores the launch address's origin on `HttpContext.Items`, and
   `Cache-Control: no-store` is set.
7. The response is a small HTML page (`HandOffPage`): a form `method="post" action="<launch url>"` with hidden fields `access_token`,
   `token_type`, `expires_in`, a "Continue" button, a "Choose another" link, and one inline line of script that submits the form at
   once. Every value is HTML-encoded.

Error codes mapped by `ProductEndpoints.Code`: 429 `rate_limited`; 403 with `tenant_suspended` -> `tenant_suspended`; other 403 ->
`not_available`; 401 -> `expired`; anything else -> `unavailable`. `ChooseProduct.razor` turns these into the banners (for example
"You're going a little fast ... Try again in N seconds", "Please try again ... Your sign-in needed renewing").

`SecurityHeaders.AllowFormPostTo` makes the CSP of that one response read `form-action 'self' <origin>`; every other response keeps
`form-action 'self'`.

- Data: none in the web app. The API tables behind the product list: `applications` (`is_active`, `launch_url`), `subscriptions`,
  `application_access`, `tenant_memberships`.
- Libraries and services: the API's token exchange.
- Background workers: none.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| Launch address per product | none | the platform console, `PUT /api/auth/admin/applications/{id}/launch-url` | No address: the product shows "It isn't ready to open yet. Ask the platform administrator to set where it opens." |
| `Auth:Tokens:ProductTokenMinutes` (API) | 15 | `src/AuthService.Api/Tokens/IdentityTokenOptions.cs` | How long the handed-over token lives. |
| Fixed in code: `wait` clamp 1 to 3600, default 60 | `ProductEndpoints.OpenAsync` | `Security/ProductEndpoints.cs` | Countdown shown after a 429. |

### Failure modes and production debugging

- **What the person sees.** A redirect back to the picker with `error=` and a banner: `not_available` ("... can't be opened right now"),
  `tenant_suspended`, `rate_limited`, `expired`, `unavailable`. HTTP 400 with an empty body for a missing anti-forgery token or a post
  that is not a pair of GUIDs.
- **What is logged / audited.** The web app logs nothing here (and never the token). The API records the exchange on its side.
- **Edge cases.**
  - The exchange is made with the person's identity token each time, so a product open is one extra API call and counts against the
    exchange's rate limit.
  - To learn whether the person is an admin, the product picker calls `ConsoleSession.LoadAsync`, which trades for a console token (and
    caches it) when none is cached. That is one extra API call on the first picker load. A rate limit there never stops the person
    choosing a product (test `ARateLimitedConsoleExchangeNeverStopsThePersonChoosingAProduct`).
- **Likely production causes.**
  - *The hand-off page does nothing.* The inline script did not run (blocked by a browser extension, or the script policy if its nonce
    is missing), in which case the Continue button still works. Otherwise the product's launch address rejects the post. Check the
    product's endpoint accepts `POST` of `application/x-www-form-urlencoded` with `access_token`, `token_type`, `expires_in`
    (decision 0007), and the address is `https` (the API refuses `http` except localhost).
  - *Blank or blocked after the post.* The CSP allows the form to post to the launch origin only. **Assumption (confirm):** some browsers
    also apply `form-action` to a redirect that follows the post, so a product that answers with a redirect to a different origin could
    be blocked. Confirm in the browser console for a "Refused to send form data" or "violates the Content Security Policy" message.
  - *Product shows "not available" to everyone.* Read-only SQL:
    `select key, is_active, launch_url from applications order by key;` and check `subscriptions` and `application_access` for the
    organisation. A product is opened only when it is active, subscribed, active for the person's seat, and has a launch URL.
  - *"Nothing to open here yet" for an organisation.* Same causes; also the organisation or the subscription may be suspended
    (`tenants.status`).

### Decisions and open questions

- **Form post, not a query string, fragment or redirect** (decision 0007): a query string is kept in history, logs and referrers. A
  short-lived single-use code the product redeems with its own credentials is the stated safer next step; not built.
- **The token passes through the person's browser for an instant**, as a form field. It is a 15-minute token for one organisation and
  product.
- **Launch address from the API only**, so a request cannot choose where a token goes.
- **Inconsistency with the README:** the README's picker section quotes "Not included in Acme Ltd's subscription." The code says
  "`<organisation>` hasn't subscribed to `<product>`." Trust the code.
- **Risk:** the hand-off page has an inline `<script>`. When a script-source policy is added (see the headers section) this one line needs
  a hash or nonce, or the auto-submit stops (the Continue button would still work).

---

## The tenant console

### What it does

The tenant console (`/console/{tenantId}/...`) is where an organisation's admins manage their own organisation: members, invitations,
roles, who can open which product, API keys, subscriptions (read only) and the audit log. Who may see or change each thing is decided
by permissions on the person's console token.

Pages and the permission each needs (`Security/ConsoleSession.cs` `PermissionKeys`; levels `ReadOnly` < `CreateRead` < `Full`):

| Page | Route | Needed to see | Needed to change |
|---|---|---|---|
| Members | `/console/{id}/members` | `members.read` (ReadOnly) | `members.manage` (Full) to enable or disable; `members.invite` (Full) to invite; `roles.assign` (Full) to open a member's roles |
| Invitations | `/console/{id}/invitations` | `members.read` | `members.invite` (Full) to invite, resend or withdraw; inviting also needs `roles.read` |
| Roles | `/console/{id}/roles` | `roles.read` | `roles.manage` (Full) |
| Role editor | `/console/{id}/roles/new`, `/roles/{roleId}` | `roles.read` and `roles.manage` (Full) | same |
| Member roles | `/console/{id}/members/{membershipId}/roles` | `roles.assign` (Full), `roles.read`, `members.read` | same |
| Product access | `/console/{id}/access` | `access.manage` (ReadOnly), `subscriptions.read`, `members.read` | `access.manage` (Full) |
| API keys | `/console/{id}/api-keys` | `apikeys.read` | `apikeys.manage` (Full); creating also needs `roles.read` |
| Subscriptions | `/console/{id}/subscriptions` | `subscriptions.read` | none (read only; plan and limits from `GET /api/auth/tenants/me/subscriptions/{id}/entitlements`, ENT-06) |
| Audit log | `/console/{id}/audit` | `audit.read` | none |

Rules:

- **A person must be an active member** of the organisation. Otherwise the console is closed ("You don't have access"), except that an
  active member of a suspended organisation is told it is suspended (`tenant_suspended`; nobody else learns this).
- **Permission-aware.** Navigation shows only built pages the person can read. A write they may not make is a disabled button with a
  tooltip naming the permission, or absent. A forced address (`?disable=`, `?delete=`, `?revoke=`, `?invite=1`, `?create=1`) shows no
  dialog to someone who cannot act. A list the API refuses with 403 reads as "You can't see ...", not an error. The API still
  enforces everything (`PermissionAwareConsoleTests`).
- **A permission change reaches the person later.** The page tells them "within about 15 minutes", because their console token is
  cached until it nearly expires.
- **Console writes are plain form posts** that redirect back with a short notice or error code in the address (never a name or a
  token), so a reload does not repeat the change.
- **One-time secrets** (a new API key, a new product secret) are shown once. See below.
- **Owner role** is identified in the pages by the role *name* `Owner`. Only an Owner is offered the Owner role (when inviting or
  assigning roles).
- **Limits in the pages**: invite up to 20 addresses at a time (one API call each), 1 to 10 roles per invitation or member, a role
  name up to 100 characters, rotate grace 0 to 720 hours (choices: not at all, 1 hour, 24 hours (recommended), 3 days, 7 days, 30 days),
  the invitation resend countdown is 60 seconds (`CooldownLimit`; the API enforces the real wait).

### How it is implemented

**How the console obtains and caches a token** (`Security/ConsoleSession.cs`):

1. `ConsoleSession.LoadAsync(httpContext, tenantId)` is scoped per request, so the layout and the page share one answer.
2. No `sid` claim: `ConsoleOutcome.SessionEnded`.
3. `ConsoleTokenCache.Find(sid, tenantId)`: a cached, unexpired `ConsoleContext` is returned (`Ready`).
4. Else `SessionService.GetAccessTokenAsync` (renews first). None: `SessionEnded`.
5. `GET /api/auth/users/me` with the identity token. 401: `SessionEnded`. Other failure: `Unavailable` (a 429 sets `RetryAfterSeconds`).
6. The person must have an `Active` membership in `tenantId`, else `Denied`.
7. `POST /api/auth/token/exchange` for `(tenantId, BuiltInApplicationId)`, where `BuiltInApplicationId` is
   `00000000-0000-7000-8000-00000000a07a` (the Auth Service as its own product, decision 0005). 401: `SessionEnded`; 403: `Denied`
   (with `SuspendedTenantName` set when the code is `tenant_suspended`); anything else: `Unavailable`.
8. `ProductTokenClaims.ReadPermissions` reads the token's `permissions` claim (`key:Level` strings) into a map; where a key appears twice
   the higher level wins. The signature is not checked (the API does that on every call).
9. `ConsoleTokenCache.Store` keeps the `ConsoleContext` for `expires_in - 60` seconds, keyed by `(session id, tenant id)`.

`ConsoleContext.Can(key, minimum)` is what pages ask. `Refresh(httpContext, tenantId)` drops one cached token (after the API answered
401); `ForgetTokens` drops all of a session's tokens (on `/choose-organisation`). The token is never printed (`ToString` is
`Console of {TenantId}`).

**Page pattern.** Every console page: injects `ConsoleSession`; in `LoadAsync` opens the console; on `SessionEnded` redirects to
`/login?returnUrl=<page>&reason=expired`; on `Denied` shows `ConsoleDenied`; on `Unavailable` or a load failure shows `ErrorState`; on
401 from the API calls `Session.Refresh` and reloads the page once; on 403 shows "you can't see this"; otherwise reads data with the
console token. A dialog or confirmation is a query-string state (`?invite=1`, `?disable=<id>`, `?rotate=<id>`) rendered as an HTML
modal, and a posted `@formname` form is made present on the page before the post arrives.

**Pages and the API routes they use** (all as the console token, over `AuthApiClient`):

| Page | Reads | Changes |
|---|---|---|
| Layout (`TenantLayout.razor`) | `GET /api/auth/tenant/me` (name and status badge) | none |
| Members | `GET /api/auth/tenants/me/members` | via `ConsoleEndpoints`: `PATCH /api/auth/tenants/me/members/{id}` |
| Invitations | `GET /api/auth/tenants/me/invitations`, `.../subscriptions`, `GET /api/auth/roles`, members | `POST /api/auth/tenants/me/invitations`; via endpoints: `POST .../invitations/{id}/resend`, `DELETE .../invitations/{id}` |
| Roles | `GET /api/auth/roles` | via endpoint: `DELETE /api/auth/roles/{id}` |
| Role editor | `GET /api/auth/tenants/me/permissions`, `GET /api/auth/roles` | `POST /api/auth/roles`, `PATCH /api/auth/roles/{id}` |
| Member roles | members, roles | `PUT /api/auth/tenants/me/members/{id}/roles` |
| Product access | `.../subscriptions`, members | via endpoint: `PUT` or `DELETE /api/auth/tenants/me/members/{id}/applications/{appId}` |
| API keys | `GET /api/auth/apikeys`, `GET /api/auth/tenants/me/permissions` | `POST /api/auth/apikeys`; via endpoint: `POST /api/auth/apikeys/{id}/rotate`, `DELETE /api/auth/apikeys/{id}` |
| Subscriptions | `GET /api/auth/tenants/me/subscriptions` | none |
| Audit log | `GET /api/auth/tenants/me/audit?limit=&action=&actorId=&before=` | none |

**The form-post endpoints** (`Security/ConsoleEndpoints.cs`, all `AllowAnonymous` because each checks sign-in by hand and redirects to
login; all validate the anti-forgery token first, and a failure is an empty HTTP 400):

| Verb and route | Handler | API call | Result redirect |
|---|---|---|---|
| `POST /console/{t}/members/{m}/status` (`status` = `Active` or `Disabled`, else 400) | `SetMemberStatusAsync` | `PATCH .../members/{m}` | `?notice=enabled\|disabled&m=` or `?error=<code>&m=` |
| `POST /console/{t}/invitations/{m}/resend` | `ResendInvitationAsync` | `POST .../invitations/{m}/resend` | `?notice=resent&m=`, or `?error=resend_too_soon&m=&wait=` (also `invitation_not_pending`, `no_seat_available`, `not_found`, `forbidden`, `unavailable`) |
| `POST /console/{t}/invitations/{m}/revoke` | `RevokeInvitationAsync` | `DELETE .../invitations/{m}` | `?notice=revoked`, or `?error=invitation_not_pending`, `not_found`, `forbidden` or `unavailable` with `&m=` |
| `POST /console/{t}/roles/{r}/delete` | `DeleteRoleAsync` | `DELETE /api/auth/roles/{r}` | `?notice=deleted`, or `?error=` |
| `POST /console/{t}/access/{a}/grant` (`membershipId` in the form) | `GrantAccessAsync` | `PUT .../members/{m}/applications/{a}` | `?notice=granted&m=&a=` or `?error=` |
| `POST /console/{t}/access/{a}/{m}/revoke` | `RevokeAccessAsync` | `DELETE .../members/{m}/applications/{a}` | `?notice=revoked&m=&a=` or `?error=` |
| `POST /console/{t}/api-keys/{k}/rotate` (`graceHours` 0 to 720, else `bad_grace`) | `RotateApiKeyAsync` | `POST /api/auth/apikeys/{k}/rotate` | `?reveal=<ref>&rotated=1&oldEnds=` |
| `POST /console/{t}/api-keys/{k}/revoke` | `RevokeApiKeyAsync` | `DELETE /api/auth/apikeys/{k}` | `?notice=revoked&m=` or `?error=` |

The error codes passed through to the page are fixed lists, for example members: `owner_role_restricted`, `last_owner`,
`membership_invited`, `no_seat_available`, `not_found`; roles: `role_in_use`, `owner_role_immutable`, `not_found`; access: `no_seat_available`,
`membership_disabled`, `application_not_subscribed`, `not_found`; keys: `api_key_not_active`, `not_found`. Anything else becomes
`forbidden` (for 403) or `unavailable`. On 401 the endpoint calls `ConsoleSession.Refresh` and redirects to the plain page, so the
next load trades for a fresh token. For `no_seat_available` the product names are cut out of the API's `detail` text after `" in: "`
(`SeatProducts`) and passed as `seats=`. That is a parse of English text; if the API changes the sentence, the names disappear.

**One-time secrets** (`Security/OneTimeSecrets.cs`, a singleton):

1. After the API returns a raw secret (a new API key, a rotated key, a new or replaced product secret), the page or endpoint calls
   `Put(sessionId, secret)`. It returns a random 16-byte hex reference. The secret itself is not put in any address.
2. The response redirects to the page with `?reveal=<reference>`.
3. On load the page calls `Take(sessionId, reference)`. It returns the secret once and removes it. It returns null if the reference is
   unknown, belongs to another session, was already taken, or is older than 2 minutes.
4. The page shows the secret in a dialog with "You won't see this again", a read-only field and (once scripts load) a Copy button. A
   reload finds nothing.

Expired entries are purged whenever `Put` runs.

**The audit log page** (`Pages/Console/Audit.razor`). Newest first. Filters: what happened (`action`), person (`actor`), from and
"up to and including" dates (`yyyy-MM-dd`; "up to" is sent as `before` = start of the next day). "Load more" raises `take` by 50 up to
200 (`Step`, `MaxTake`), which is sent as `limit`. The start date is applied in the page, after the API answers, by dropping older
entries. The actor is named: "You" for the signed-in person, the API's `actorName` if given, else "A person (a1b2c3d4)", a product by
name (if the person may read subscriptions), "Auth Service" for the system. Action codes are turned into words by `AuditLabels`
(`Api/AuditContracts.cs`); an unknown code reads as its last words. The organisation always comes from the token, never the address.

**Role editor.** Sections come from `GET /api/auth/tenants/me/permissions` (the Auth Service first, then each product in use), so every
choice offered is one the API accepts. Only permissions above "No access" are sent; `PATCH` replaces the whole set. Permissions the role
holds for a product that is no longer active are listed in a warning; saving removes them. The built-in Owner role (`IsBuiltIn`) cannot
be edited.

- Data: none in the web app. API tables behind it: `tenants`, `tenant_memberships`, `roles`, `role_permissions`, `membership_roles`,
  `invitations`, `api_keys`, `api_key_permissions`, `subscriptions`, `application_access`, `audit_entries`.
- Background workers: none in the web app. An invitation email depends on the API's email worker.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:ProductTokenMinutes` (API) | 15 | `IdentityTokenOptions.cs` | How long a console token (and so a stale permission) lasts. The web cache keeps it `expires_in - 60` seconds. |
| Built-in application id | `00000000-0000-7000-8000-00000000a07a` | `ConsoleSession.BuiltInApplicationId` | Must match the API's seeded Auth Service product, or every console exchange fails. |
| Fixed in code | one-time secret lifetime 2 minutes | `OneTimeSecrets.Lifetime` | A person who is slow to follow the redirect loses the secret. |
| Fixed in code | audit step 50, max 200 | `Audit.razor` | Matches the API's page maximum (200). |

### Failure modes and production debugging

- **What the person sees.**
  - *Not an active member, or the exchange refused (403):* "You don't have access" (`ConsoleDenied`).
  - *Suspended organisation, active member:* "`<name>` is suspended" on every console page.
  - *API unreachable, 5xx, or the exchange answers something else:* "We couldn't load the ..." with a `Reference` code.
  - *Rate limited while opening the console (429):* "You're going a little fast" with a countdown from `Retry-After` (default 60 seconds,
    clamped 1 to 3600); no further exchange is attempted on that page.
  - *403 from a list although the token seemed to allow it:* "You can't see ...".
  - *Anti-forgery failure on a console post:* HTTP 400, empty.
  - *Session ended:* redirect to login with `reason=expired`.
- **What is logged / audited.** The web app logs nothing for these. The API audits the changes: for example `auth.membership.disabled`,
  `auth.membership.enabled`, `auth.membership.roles_changed`, `auth.invitation.sent`, `auth.invitation.resent`, `auth.invitation.revoked`,
  `auth.role.created`, `auth.role.updated`, `auth.role.deleted`, `auth.membership.access_granted`, `auth.membership.access_revoked`,
  `auth.apikey.created`, `auth.apikey.rotated`, `auth.apikey.revoked`, `auth.tenant.audit_read`. Read-only SQL for the last day of
  console changes: `select occurred_at, action, actor_id, tenant_id from audit_entries where tenant_id = '<id>' order by occurred_at desc limit 50;`
- **Edge cases and half-states.**
  - *Bulk invitation:* each address is a separate API call. If some fail, the dialog stays open with only the addresses still to send, and
    a list of reasons. A 401 mid-way refreshes the token and reloads the page (addresses typed are lost).
  - *A secret created but never shown.* If the API call succeeded and the redirect or the 2-minute window was missed, the key exists
    at the API but its value is lost. Create or rotate a new one.
  - *"Owner" is matched by name.* The Owner role is detected as `role.Name == "Owner"` in the Members, Invitations and Member roles
    pages, and whether the caller is an Owner by matching the console email against the member list. The API remains the authority.
  - *Audit with a start date* filters in the page: a page of up to 200 entries may all be older than the start and show nothing, with
    "Load more" hidden.
- **Likely production causes.**
  - *Console shows "You don't have access" to a real admin.* They are not an `Active` member (invited or disabled): `select u.email,
    m.status from tenant_memberships m join users u on u.id = m.user_id where m.tenant_id = '<id>';`. Or the exchange for the built-in
    product is refused.
  - *Console shows pages missing from the sidebar.* The token's `permissions` lack the read permission; the sidebar shows only pages the
    person can read at ReadOnly or above. Compare the person's roles on the Members page.
  - *A permission change is not visible.* The cached token is up to about 14 minutes old (`expires_in - 60`). It is dropped on a 401
    or when the person visits `/choose-organisation`. This is by design; the page says "within about 15 minutes".
  - *Every console page says "We couldn't load" with a countdown.* The API is rate limiting the token exchange for this person or address.
  - *API key secret "not shown".* See the symptom table.
  - *Audit entry reads "Launch url set".* The code `auth.application.launch_url_set` has no entry in `AuditLabels`, so it is shown by its
    last words. `AuditLabels` is a hand-kept copy of `AuditActions`.

### Decisions and open questions

- **A console token per (session, organisation)**, cached on the server for nearly its whole life, so browsing does not trade a token on
  every page (test `BrowsingTheConsoleRepeatedlyMakesOneExchange`). The cost is staleness of permissions.
- **Redirect-after-post with codes in the address** keeps names and tokens out of URLs and makes reloads safe.
- **Inconsistent / drift risks:**
  - `AuditLabels` is not generated from `AuditActions`. `AuditActions.ApplicationLaunchUrlSet` (`auth.application.launch_url_set`) has no
    label and is not in either filter list. Nothing in the tests checks that every action is labelled.
  - The web app hard-codes API error code strings in each page and endpoint. A renamed code on the API silently falls through to
    "unavailable" or "forbidden".
  - `SeatProducts` reads English text from the API's `detail`.
  - The Owner role is found by name rather than by `IsBuiltIn`.
  - `GetAllAsync` caps at 10,000 rows silently.
- **Assumption (confirm):** the "about 15 minutes" wording equals `Auth:Tokens:ProductTokenMinutes` (15). The text is fixed in the pages, not
  read from the setting.
- The `Invitations` page tells the person product names only when they may read subscriptions; otherwise it shows a count.
- The invitation expiry shown ("Expires") and the "Expired" status come from the API (`IsExpired`); the web app does not compute them.

---

## The platform console

### What it does

The platform console (`/platform/...`) is for the people who run the whole platform, never for an organisation's admin. It is
desktop only (a "Use a larger screen" notice shows below 1024 px; the CSS media query is `min-width: 1024px`). Its header always
says "Platform admin". The organisation console never links to it.

Parts: Products, Organisations, Plan history, Webhook events, Signing keys, Audit log. `/platform` redirects to `/platform/audit`.

Rules:

- **The API decides who may enter.** The web app does not know who a platform admin is. `PlatformSession` makes one cheap read that
  only platform admins may make, `GET /api/auth/admin/webhooks?limit=1`, using the person's own identity token. 200 means allowed;
  403 means not allowed; 401 means the sign-in ended; anything else means the console is unavailable. It never trusts a claim, a
  cookie or an address from the browser.
- **Fail closed.** The layout draws nothing of the page behind it until the API has answered. A person who is not allowed gets a
  plain "You don't have access to this page" with HTTP status 403 and no navigation, and the page's own data load is never made.
- **The probe is not audited** (the comment on `ProbePath` says it is the cheapest read behind the policy that is not itself recorded in
  the audit log). Viewing the platform audit log is recorded (`auth.admin.audit_read`).
- **The platform token is the person's identity token**, not a product token.
- **Platform admin is a database flag**, `users.is_platform_admin`, read by the API on every request (`PlatformAdminHandler`). There is
  no way to become one through the website.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `GET /platform` | none | `Program.cs` `MapGet("/platform")` | n/a | Redirects to `/platform/audit`. |
| `GET /platform/products` | `[Authorize]` + API probe | `Pages/Platform/PlatformProducts.razor` | `GET /api/auth/admin/applications` | Lists every product, available or not. |
| `GET /platform/products/{id}/permissions` | same | `PlatformPermissions.razor` | `GET /api/auth/admin/applications/{id}/permissions` | A product's permission list. |
| `GET /platform/products/{id}/roles` | same | `PlatformRoleTemplates.razor` | `GET /api/auth/admin/applications/{id}/role-templates`, `PUT` the same route, `POST .../role-templates/apply` | The roles a product ships with, editing the whole set as JSON, and copying them to organisations (ONB-04). |
| `GET /platform/organisations` | same | `PlatformOrganisations.razor` | `GET /api/auth/admin/tenants` | Every organisation by name with status, people and creation date; `?q=` filters by name here; `?org={id}&to=Suspended\|Active` opens the confirmation (only for an organisation in the list that would really change). |
| `GET /platform/products/{id}/limits` | same | `PlatformLimits.razor` | `GET` and `POST /api/auth/admin/applications/{id}/limits` | A product's limits; add or edit one (ENT-06). |
| `GET /platform/products/{id}/plans` | same | `PlatformPlans.razor` | `GET .../plans`, `PUT .../plans/{key}/active`, `DELETE .../plans/{key}` | Plans; activate, deactivate, delete (ENT-06). |
| `GET /platform/products/{id}/plans/new`, `.../plans/{key}/edit` | same | `PlatformPlanEditor.razor` | `POST .../plans`, `POST .../plans/{key}/preview`, `PUT .../plans/{key}` | Create, copy (`?from=`) or edit a plan; an edit is previewed first (ENT-06). |
| `GET /platform/organisations/{id}` | same | `PlatformOrganisationSubscriptions.razor` | `GET /api/auth/admin/tenants`, `GET /api/auth/admin/tenants/{id}/subscriptions` | An organisation's products and plans (ENT-06). |
| `GET /platform/organisations/{id}/products/{productId}` | same | `PlatformSubscriptionEntitlements.razor` | `GET .../entitlements`, `PUT .../plan`, `PUT`/`DELETE .../overrides/{key}` | Plan, effective limits and overrides (ENT-06). |
| `GET /platform/entitlement-history` | same | `PlatformEntitlementHistory.razor` | `GET /api/auth/admin/entitlement-history?limit=&applicationId=&tenantId=&planKey=` | Plan history (ENT-06). |
| `GET /platform/webhooks` | same | `PlatformWebhooks.razor` | `GET /api/auth/admin/webhooks?limit=&status=&applicationId=` and `GET /api/auth/applications` | Webhook events, newest first. |
| `GET /platform/signing-keys` | same | `PlatformSigningKeys.razor` | `GET /.well-known/jwks.json` (no token) | The published public signing keys. |
| `GET /platform/audit` | same | `PlatformAudit.razor` | `GET /api/auth/admin/audit?limit=&action=&tenantId=&actorId=&before=` | The audit log across every organisation. |
| `POST /platform/products/{id}/active` (`isActive` true or false) | session + probe | `PlatformEndpoints.SetProductActiveAsync` | `PUT /api/auth/admin/applications/{id}/active` | Make a product available, or withdraw it. |
| `POST /platform/products/{id}/rotate-secret` (`graceHours` 0 to 720) | session + probe | `RotateProductSecretAsync` | `POST /api/auth/admin/applications/{id}/secret/rotate` | Replaces the client secret; shown once. |
| `POST /platform/signing-keys/rotate` (`confirm` = `ROTATE`) | session + probe | `RotateSigningKeyAsync` | `POST /api/auth/admin/signing-keys/rotate` | Emergency signing-key rotation. |
| `POST /platform/webhooks/{id}/resend` | session + probe | `ResendWebhookAsync` | `POST /api/auth/admin/webhooks/{id}/resend` | Re-queues a failed event. |
| `POST /platform/organisations/{id}/status` (`status` Active or Suspended, `reason` a listed code) | session + probe | `SetOrganisationStatusAsync` | `PUT /api/auth/admin/tenants/{id}/status` | Suspends or reactivates. A status that is neither is `400`; a reason that is not listed sends nothing and redirects to `?org=&to=&error=choose_reason`; success redirects to `?notice=suspended\|reactivated&m=`; the API's `tenant_not_found`, a 403 and any other failure to `?error=tenant_not_found\|forbidden\|unavailable&m=`. |
| Forms on the pages (`@formname`) | session + probe | `PlatformProducts.razor` `RegisterAsync`, `RenameAsync`, `CreateTestOrganisationAsync`, `SubscribeOrganisationAsync`, `SaveLaunchAsync`; `PlatformPermissions.razor` `AddAsync` | `POST /api/auth/admin/applications`, `PUT .../{id}/name`, `POST .../{id}/test-organisations`, `POST .../{id}/subscriptions`, `PUT .../{id}/launch-url`, `POST .../{id}/permissions` | Register a product, rename it, create a test organisation, subscribe an organisation, set its launch address, add permissions. |

Flow of any platform page:

1. `PlatformLayout.razor` `OnInitializedAsync` calls `PlatformSession.LoadAsync` (one answer per request).
2. `PlatformSession.OpenAsync`: no `sid` claim or no access token -> `SessionEnded`. Probe 200 -> `Allowed` with the token; 401 ->
   `SessionEnded`; 403 -> `NotAllowed`; other -> `Unavailable`.
3. The layout: `SessionEnded` redirects to `/login?returnUrl=<path>&reason=expired`; `NotAllowed` sets the response to 403 and shows
   the "not allowed" page; `Unavailable` shows `ErrorState` ("We couldn't open the platform console") with a `Reference`; `Allowed` draws
   `@Body`.
4. Each page asks the same `PlatformSession` again (same answer) and returns without loading data unless `Allowed`.
5. Forms: the endpoint validates the anti-forgery token, checks the person is signed in, calls `platform.LoadAsync`, and only then
   calls the API, so a refused person changes nothing. Results redirect with `notice=` or `error=` codes.

Details by part:

- **Products.** Lists name, key, status ("Available" or "Not available" with what to do next), permission count and launch address
  ("Opens at"). Actions: Register a product (name up to 200, key up to 63 characters, token audience up to 200, left empty it becomes the
  key plus `-api`; optionally the address it opens at, the permissions as lines or pasted JSON read by `PermissionText`, with a "Load from
  a file" button that needs the script, and a "Make it available now" box that is only sent when permissions were given; one API call saves
  it all or nothing; the client secret is shown once through `OneTimeSecrets` as the one string with the client id in it (`credential`; a
  bare secret from an older API), with the audience, client id, address and permission count beside it and, when the API's discovery
  document can be read, a "Settings for the product" box with `AuthService__Url` and `AuthService__ClientSecret` to copy (the same box
  appears after Replace secret)), Rename (the display name only; the dialog says the key and audience stay), Test organisation and Subscribe organisation (offered only
  for an available product; the first creates an organisation owned by an existing verified person, by email, with optional seats, and
  subscribes it, then shows its id; the second subscribes an organisation by its id, which the tenant console shows; both explain the
  API's reasons in plain words: no account, email not verified, access removed, unknown or suspended organisation, product withdrawn),
  Make available (offered only when the product has permissions), Withdraw (asks first; existing
  subscriptions and seats are not changed), Opens at (an https address, no user name, password or fragment, at most 2000 characters;
  the API's reasons are shown in the dialog), Replace secret (grace 0 to 720 hours, same choices as API keys), Permissions. A product
  becomes available only after it has permissions (`no_permissions_registered`).
- **Roles.** (`/platform/products/{id}/roles`) Lists the roles a product ships with (name, what it is for, and each permission with its level in words: View only, View and create, Full access). "Edit roles" opens the whole set as JSON (`RoleTemplateText`, with "Load from a file" when the script runs); a role left out is removed, `{"roles": []}` removes them all, and the API's reasons (an unknown key, a bad level, a repeated name) appear in the dialog. "Copy to organisations" (offered only when there are roles) asks first, saying nothing is overwritten, and reports how many roles were created, in how many organisations, and how many were left alone. The register dialog reads a `roles` list from the same JSON it reads permissions from and sends it with the registration.
- **Limits, plans and an organisation's plan (ENT-06).** Values are typed as whole numbers (thousands separators allowed) and read by
  `EntitlementText.TryRead`; "Unlimited" sends null; an empty plan value means "use the default" and is left out of the plan. Values are shown by
  `EntitlementText.Describe` ("75,000 contacts", "Unlimited", "On"/"Off" for a `flag`). A plan edit first calls the preview and, when it changes
  anyone's numbers, shows how many and waits for "Save and update N organisations" (the form posts again with `confirm=1`). Forms are Blazor forms
  (`@formname` `limit`, `plan`, `active-{key}`, `delete`, `override`, `clear`). The history shows null old and new values as a dash, because the
  API sends null both for "unlimited" and for "nothing before".
- **Permissions.** Lists a product's catalog by key. "Add permissions" takes one per line as `key | label | optional description`, or pasted JSON (`PermissionText`).
  The list only grows; adding an existing key changes its label and description. The page says "Up to 200 at a time"
  (`MaxPerPost`) but does not enforce it; the API does.
- **Webhook events.** Newest first; filter by status (`Pending`, `Delivered`, `Failed`) and product; "Load more" up to 200. A
  `Failed` event offers "Send again" (asks first). Callback addresses and secrets are never shown. Times are UTC.
- **Signing keys.** Shows the public keys the API publishes (id, type and curve, algorithm, use). "Rotate now" is an emergency action:
  the dialog says a new key signs at once, every earlier key leaves the list so every token already issued stops working, people have to
  sign in again including you, and it is audited under the admin's name. The admin must type the word `ROTATE`
  (`PlatformEndpoints.RotationWord`, compared ignoring case after trimming). If it does not match, the API is not called.
- **Audit log.** Everything recorded across every organisation. Filters: what happened, organisation id (must be a GUID, else a
  "Check the organisation id" message), from and to dates, "Only this person". "Load more" up to 200.

- Data: none in the web app. API tables: `users` (`is_platform_admin`), `applications`, `application_secrets`, `permissions`,
  `webhook_outbox`, `signing_keys`, `audit_entries`.
- Background workers: none in the web app. The API's webhook worker moves events from `Pending` to `Delivered` or `Failed`. If it is
  not running, events stay `Pending`.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| Platform admin flag | false | `users.is_platform_admin` (API database) | Decides who passes the probe. See the first-admin step in [deployment.md](../deployment.md). |
| Probe route | `/api/auth/admin/webhooks?limit=1` | `PlatformSession.ProbePath` | If the API changes that route or its policy, the guard breaks (everyone "not allowed" or "unavailable"). |
| Rotation word | `ROTATE` | `PlatformEndpoints.RotationWord` | The word an admin types. |
| Product registration limits | name 200, key 63, audience 200, address 2000 | `PlatformProducts.razor` input `maxlength` | Browser-side only; the API validates. |

### Failure modes and production debugging

- **What the person sees.**
  - *Not a platform admin:* HTTP 403 and "You don't have access to this page", with a link to "Go to my organisations".
  - *Signed out or token refused (401):* redirect to sign-in with `reason=expired`.
  - *API unreachable or failing:* "We couldn't open the platform console" with a reference. It does not say "not allowed".
  - *A form whose confirm word is wrong:* `?rotate=1&error=confirm` and "That isn't the word. Nothing was rotated."
  - *Codes shown on product forms:* `no_permissions_registered`, `application_not_found`, `bad_grace`, `forbidden`, `unavailable`;
    webhooks: `webhook_not_failed`, `webhook_not_found`; registration: `application_key_taken`, `audience_taken`.
- **What is logged / audited.** The web app logs nothing. The API audits `auth.application.registered`,
  `auth.application.secret_rotated`, `auth.application.permissions_registered`, `auth.application.activated`,
  `auth.application.deactivated`, `auth.application.launch_url_set`, `auth.webhook.resent`, `auth.signing_key.rotated`,
  `auth.admin.audit_read`.
- **Edge cases.**
  - The probe is a normal list call, so a platform admin who is rate limited by the API sees "We couldn't open the platform console".
  - After an emergency signing-key rotation the admin's own token stops working too; their next page redirects to sign-in (and see the
    possible loop under the session section).
  - A product registered with a secret that was never shown (missed the 2-minute window) has no recoverable secret. Replace the secret.
- **Likely production causes.**
  - *A real admin gets "not allowed".* The flag is not set. Read-only: `select email, is_platform_admin from users where email = '<address>';`.
  - *The console says "unavailable" for an admin.* The probe got a 5xx or 429, or the API is unreachable.
  - *Webhooks stay Pending.* The API's webhook worker is not running or cannot reach the product; see the webhooks part.
  - *Product shows "Not available".* No permissions yet, or it has been withdrawn: `select key, is_active, launch_url from applications;`.

### Decisions and open questions

- **Probe by a real admin-only read** instead of a claim, so granting or revoking the flag takes effect on the next request and no
  token can carry the privilege.
- **A 403 status code** for "not allowed" is set from inside the layout (`HttpContext.Response.StatusCode`), because the page body is
  still rendered.
- **Open question:** `PlatformSession` and the console endpoints make one probe per request. A platform page that makes a form post
  costs two API calls before the real one. Acceptable at this scale; not measured.
- **Inconsistency:** the README says the platform console has "Webhook events" and "Signing keys" and "Audit log" and "Products". The
  sidebar (`PlatformLayout.razor` `Items`) lists exactly those four. Permissions is reached from a product row, not the sidebar.
- **Unknown:** whether the API's webhook list endpoint is audited (the comment says it is not). Confirm in
  `src/AuthService.Api/Features/Webhooks/WebhookAdminEndpoints.cs`.

---

## Security headers, anti-forgery, safe redirects and error pages

### What it does

Protections that apply to the whole console.

- **Security headers on every response** (`Security/SecurityHeaders.cs`, `UseSecurityHeaders`, set in `OnStarting`):
  - `Referrer-Policy: no-referrer`
  - `X-Content-Type-Options: nosniff`
  - `X-Frame-Options: DENY`
  - `Content-Security-Policy: frame-ancestors 'none'; base-uri 'self'; form-action 'self'[ <origin>]; object-src 'none'; script-src 'self' 'nonce-<n>'`
  - `Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=()`
  - `Cache-Control: no-store` on every `text/html` response
- **Script policy (AUTH-271).** `script-src 'self' 'nonce-<n>'`: scripts load only from this site (`blazor.web.js`, `app.js`,
  `ReconnectModal.razor.js`), and the only inline scripts carry the response's nonce: the import map (`App.razor`, `<ImportMap nonce>`) and the
  hand-off page's one line (`ProductEndpoints.HandOffPage`). `SecurityHeaders.Nonce(context)` makes 18 random bytes, base64, once per response.
  No `unsafe-inline`, no `unsafe-eval`, no inline event handlers. `Security:ScriptPolicyReportOnly` (`SecurityHeadersOptions`, default false)
  moves `script-src` to a `Content-Security-Policy-Report-Only` header for a first rollout; the rest stays enforced. `default-src`, `style-src`
  (two pages set an inline `style` width) and `connect-src` are still not set. Checked in a real browser on 2026-11-11 (sign-up, sign-in,
  verify and reset links, picker, every console and platform page, the hand-off): no violations. There is also no HSTS from the web app; the Caddy proxy sets `Strict-Transport-Security: max-age=15552000` (six months, no `includeSubDomains`).
- **Anti-forgery.** `app.UseAntiforgery()` protects Blazor form posts (`EditForm` and `<AntiforgeryToken />`). Every custom POST
  endpoint (`/logout`, `/choose-product/open`, `ConsoleEndpoints`, `PlatformEndpoints`) calls `IAntiforgery.ValidateRequestAsync` by
  hand and answers an empty HTTP 400 on `AntiforgeryValidationException`.
- **Safe redirects.** `LocalRedirect.Safe(returnUrl)` (`Security/LocalRedirect.cs`) returns the address only when it starts with a single
  `/`, is not `//host` or `/\host`, and has no control characters; otherwise `/`.
- **Error pages.** In any environment except Development, `UseExceptionHandler("/Error", createScopeForErrors: true)`. `/Error` shows
  "Something went wrong" and "Reference for support" (`Activity.Current?.Id ?? TraceIdentifier`). `UseStatusCodePagesWithReExecute("/not-found")`
  shows "Page not found" for empty 4xx and 5xx responses. The `UnhandledErrorBar` ("An unexpected error has occurred. Reload") sits in
  every layout and stays hidden until a Blazor error happens.
- **Health.** `GET /healthz` answers `ok`, says nothing about the API.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| every request | none | `Program.cs` pipeline | `UseForwardedHeaders` (if configured), `UseExceptionHandler`, `UseSecurityHeaders`, `UseStatusCodePagesWithReExecute`, `UseAuthentication`, `UseAuthorization`, `UseAntiforgery`, `MapStaticAssets` | The order matters: forwarded headers first. |
| `GET /healthz` | none | `Program.cs` | n/a | Returns `ok`. |
| `GET /Error` | none | `Pages/Error.razor` | n/a | The unhandled-exception page. |
| `GET /not-found` | none | `Pages/NotFound.razor` | n/a | The not-found page (also used for status-code re-execution). |
| Redirect after sign-in | n/a | `Login.razor`, `/logout` | `LocalRedirect.Safe` | Allows only local paths. |

- Anti-forgery tokens and cookies are protected by the same data protection keys as the session cookie.
- The security header values are set when the response starts; `SecurityHeaders.AllowFormPostTo(context, address)` can add one origin
  to `form-action` for that response only (the product hand-off).
- Libraries: ASP.NET Core Antiforgery, Authentication, Authorization.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `AllowedHosts` | `*` | `appsettings.json` | Any `Host` header is accepted; the proxy decides the real names. |
| `ASPNETCORE_ENVIRONMENT` | `Production` in the image | `Dockerfile` | In Development the exception handler is off and the session cookie is not forced `Secure`. |
| Response headers | fixed | `SecurityHeaders.cs` | Not configurable. |
| Proxy-set `Strict-Transport-Security` | `max-age=15552000` | `deploy/Caddyfile` | HSTS comes from the proxy, not the app. |

### Failure modes and production debugging

- **What the person sees.**
  - *Failed anti-forgery check:* HTTP 400. **Assumption (confirm):** because `UseStatusCodePagesWithReExecute("/not-found")` is registered and
    the 400 has no body, the person may see the "Page not found" page with a 400 status. I did not run it; the tests assert only the
    400. Common causes are a form left open across a deploy or key loss, or a stale tab.
  - *Unhandled exception in Production:* "Something went wrong" with a reference.
  - *Unknown address:* "Page not found" (no detail leaks; tested).
- **What is logged.** ASP.NET Core logs unhandled exceptions at Error. `Microsoft.AspNetCore` is set to Warning in `appsettings.json`, so
  normal request lines are not logged.
- **Edge cases.**
  - Because the headers are added in `OnStarting`, a response that has already started cannot be changed.
  - The hand-off response has `form-action` for the product origin plus `'self'`; all others have `'self'` only. A form that posts
    elsewhere is blocked by the browser.
  - `form-action 'self'` also covers the `/logout` and console posts, which are same-origin.
- **Likely production causes.**
  - *A page is broken by the script policy.* The browser console says "Refused to execute inline script" or "Refused to load the script".
    A new inline `<script>` needs `nonce="@SecurityHeaders.Nonce(HttpContext)"`; a script from another site is not allowed at all. To
    roll back quickly, set `Security__ScriptPolicyReportOnly=true` (reported, not enforced) and restart the web app.
  - *Forms rejected everywhere after a restart.* Data protection keys were lost (see the session section).
  - *Framing.* The console cannot be shown inside another site (`frame-ancestors 'none'`, `X-Frame-Options: DENY`).

### Decisions and open questions

- **No script policy yet** is a known gap, stated in the code and README. Today an injected script would not be blocked by the browser.
  The mitigations are that Razor encodes values, that pages render values with `@` or `WebUtility.HtmlEncode`, and that the hand-off page
  encodes every value. A page that uses `AddMarkupContent` builds its message from fixed text passed through `HtmlEncode` (for example
  the notice banners).
- **`form-action` per response** is a narrow, one-origin allowance rather than a general one.
- **Open question:** whether anti-forgery failures should show a clearer message than an empty 400 (and possibly the not-found page).
- **Open question:** whether `AllowedHosts` should name the real hosts rather than `*`.
- `LocalRedirect` is also used on `/logout`'s `returnUrl`, so a crafted sign-out form cannot send a person off-site.

---

## Layouts, shared components and design tokens

### What it does

A maintainer's map of where things live. Nothing here changes behaviour.

### How it is implemented

Layouts (`src/AuthService.Web/Components/Layout`):

| File | Used by | Notes |
|---|---|---|
| `MainLayout.razor` | default (`Routes.razor`), `Home`, `Error`, `NotFound` | A skip link and `<main>`. |
| `AuthLayout.razor` | sign-up, sign-in, verify email, forgot and reset password, accept invitation, `/account` | Brand panel on desktop, centred card on tablet, full width on mobile. |
| `PickerLayout.razor` | `/choose-organisation`, `/choose-product` | Top bar with the person's menu and sign-out; one centred column. |
| `TenantLayout.razor` | all `/console/{id}/...` pages | Sidebar of readable pages, header with organisation and status badge, suspended banner. Reads `GET /api/auth/tenant/me`. |
| `PlatformLayout.razor` | all `/platform/...` pages | Dark header, the API guard, "Use a larger screen" notice. |
| `ReconnectModal.razor` (+ `.css`, `.js`) | `App.razor` | Blazor's rejoin dialog. |
| `UnhandledErrorBar.razor` | every layout | Hidden until a Blazor error. |
| `LogoMark.razor` | layouts | The logo. |

Shared components (`Components/Shared`): `AlertBanner` (kinds `Error`, `Warning`, `Info`, `Success` in `AlertKind.cs`), `TextField`,
`PasswordField` (with a show/hide button revealed by `app.js`), `PasswordRules`, `EmptyState`, `ErrorState` (with the reference and a
rate-limit countdown), `ConsoleDenied`, `StatusBadge` (kind, icon and label together so colour is never the only signal), `Icon`
(a fixed set of inline icons by name), `SignOutButton` (a form post to `/logout`, never a link), `RedirectToLogin`.

Routing: `Components/Routes.razor` uses `AuthorizeRouteView`; a `[Authorize]` page reached without a valid session renders
`RedirectToLogin`, which forces a load to `/login?returnUrl=...`. `NotFoundPage` is `Pages/NotFound`.

Design tokens: `wwwroot/app.css` (about 2,000 lines). The "Signal Indigo" tokens are CSS custom properties on `:root` (for example
`--bg-canvas`, `--text-primary`, `--action-primary`, `--success`, `--warning`, `--error`, `--info`, `--platform-header`, `--font-ui`,
`--radius-*`). Dark values apply under `@media (prefers-color-scheme: dark)` for `:root:not([data-theme="light"])`, and under
`:root[data-theme="dark"]`. Breakpoints used are 640 px, 1024 px, and a `max-width: 639px` block. The Inter font is named first but not
loaded from any host, so no page calls a third party. The design source of truth is the Figma design and the portal design skill noted in
the README; I did not review it.

Other files: `wwwroot/app.js` (see the public pages section), `Properties/launchSettings.json` (development URL `http://localhost:5090`),
`appsettings.json`, `appsettings.Development.json` (API at `http://localhost:5080`).

- Data: none.
- Background workers: none.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `BlazorDisableThrowNavigationException` | `true` | `AuthService.Web.csproj` | `NavigateTo` during static rendering does not throw, so the rest of a method keeps running after a navigation. Pages guard with a `leaving` flag. |
| Theme | system preference | `app.css` | `data-theme="light"` or `"dark"` on `<html>` forces a theme. Nothing in the app sets it. |

### Failure modes and production debugging

- *Pages look unstyled.* `app.css` or the scoped bundle `AuthService.Web.styles.css` is not served: check that `MapStaticAssets` is
  active and the proxy passes `/_framework` and the fingerprinted asset paths.
- *Buttons that should appear (show password, Copy) are missing.* `app.js` did not load or failed. The hidden buttons are revealed
  only by the script. Every page still works without them.
- *A blank interactive page because the WebSocket is blocked.* **Not expected today:** no component uses an interactive render mode, so
  a blocked WebSocket (`/_blazor`) should not blank a page, and the "Rejoining the server..." dialog should never appear. If a blank
  page is seen, check that `blazor.web.js` and `app.js` load (browser network tab), then the server logs. The Caddyfile comment says
  "Blazor keeps a WebSocket open, which reverse_proxy passes through"; that matters only if a page is later made interactive.
- *A page flashes empty and then fills.* Pages render nothing while `!ready` (they load data in `OnInitializedAsync`); with static
  rendering this is one pass, so a long blank wait is a slow API call.

### Decisions and open questions

- **`BlazorDisableThrowNavigationException`** is on, so every page that navigates sets `leaving = true` and renders nothing, to avoid
  drawing a page it is about to leave.
- **Open question:** decision 0002 should be updated to say the console is statically rendered, or the pages should be made
  interactive as the decision describes. The `AddInteractiveServer*` registrations and `ReconnectModal` are present but unused.
- **Open question:** `MainLayout`, `Home`, `Error` and `NotFound` use plain, unstyled markup and read as unfinished next to the other
  layouts.

---

## Symptom to where to look (this part)

| Symptom a user or on-call engineer reports | Likely cause | First place to look (file and method, setting, table) | What to check |
|---|---|---|---|
| Everyone was signed out after a deploy | Sessions are in memory; a restart empties `MemorySessionStore` | `Security/MemorySessionStore.cs`; `deploy/docker-compose.yml` | Expected. If forms also fail (400), the data protection keys were lost: `docker compose exec web ls -l /keys` should show `key-*.xml`; `AuthService__DataProtection__KeysPath` must be `/keys` with the `webkeys` volume mounted. |
| Person is bounced to sign-in with "Your session ended" and cannot stay in | API refuses renewal (401/403) or the cookie is not kept | `SessionService.EnsureFreshAsync`; web log `A session of user {UserId} ended: the API would not renew it ({Code}).` | The `{Code}`. Check `refresh_tokens.revoked_at` for that user's family. Check the browser stores `auth_session` (it is `Secure`, so it needs HTTPS). |
| Endless redirect between a page and `/login` | **Assumption:** the API rejects the identity token while the web session still looks fresh (clock difference, signing-key rotation) and `Login` sends signed-in people straight back | `ConsoleSession.OpenAsync`, `Login.razor` `OnInitialized` | Compare web and API server clocks; check `auth.signing_key.rotated` in `audit_entries`; wait for the 15-minute token to renew, or clear the cookie. |
| A page shows "We couldn't load ..." with a reference code | The API call failed, was unreachable, rate limited, or answered something unreadable | `AuthApiClient.SendAsync`; `Components/Shared/ErrorState.razor` | The web app logs nothing for this. Look in the API logs and the proxy at that time; check `AuthService__Api__BaseUrl` and `curl .../health/ready`. The reference is the last 8 characters of the request id and may not be unique. |
| The page shows "You're going a little fast" with a countdown | The API returned 429 (often the token exchange) | `ConsoleSession.NoteRateLimit`; `ErrorState.razor` | The proxy trust settings on both apps: all visitors may share one address. `Retry-After` is the countdown. |
| Everyone is rate limited after a few sign-ins | The API sees one address for all visitors | `ClientAddressHandler`; `AuthService:Proxy`; the API's `Auth:Proxy` | Both `AuthService__Proxy__TrustedNetworks__0` (web) and `Auth__Proxy__TrustedNetworks__0` (API) must cover the stack's network (`DOCKER_SUBNET`). |
| Admin console shows no products / "Nothing to open here yet" | No product is `Available` for the person: not active, no launch address, not subscribed, no seat, or the organisation is suspended | `Pages/ChooseProduct.razor` `Reason`; tables `applications`, `subscriptions`, `application_access`, `tenants` | `select key, is_active, launch_url from applications;` and the person's seat in `application_access`. The grey card text names the reason. |
| Clicking Open on a product does nothing | The hand-off page's auto-submit was blocked, or the product's launch address rejects the post | `Security/ProductEndpoints.cs` `HandOffPage`; the product's endpoint | Press Continue on the page. Check the product accepts `POST` form fields `access_token`, `token_type`, `expires_in` and that the browser console shows no CSP error. |
| Sign-in works against the API but not in the console | Cookie not stored (plain HTTP), API unreachable from the web container, code unknown to the page, or all visitors rate limited | `SessionAuthentication.AddSessionAuthentication`; `Login.razor` `SignInAsync` | Browser dev tools for `Set-Cookie: auth_session`; `AuthService__Api__BaseUrl`; the message shown (the page maps only `invalid_credentials`, `email_not_verified`, `account_disabled`, `rate_limited`). |
| Cookies are not being set behind the proxy | The session cookie is `Secure` outside Development, so a plain-HTTP visit drops it; or the proxy strips `Set-Cookie` | `SessionAuthentication` (`CookieSecurePolicy.Always`); `deploy/Caddyfile` | Visit over HTTPS; confirm `ASPNETCORE_ENVIRONMENT`; check the response `Set-Cookie` leaves the proxy. The proxy trust settings affect addresses, not the `Secure` flag. |
| A secret (API key, product secret) is not shown a second time, or not shown at all | By design it is shown once; it also lives only 2 minutes in memory, for the same session | `Security/OneTimeSecrets.cs`; `ApiKeys.razor` `Take` | Create or rotate again. A restart, a second web instance, a different session, a reload, or waiting over 2 minutes loses it. |
| Blank page, or "Rejoining the server..." (WebSocket blocked) | Not expected today: no page uses an interactive render mode | `Program.cs`; `Components/App.razor`; `ReconnectModal.razor` | Check `blazor.web.js` and `app.js` load; read the browser console. Only if a page is made interactive does the proxy's WebSocket pass-through matter. |
| A form post answers 400 / "Page not found" | Failed anti-forgery check (form left open across a deploy, stale tab, lost keys) | the custom endpoints and `UseAntiforgery` in `Program.cs` | Reload the page and try again; if it happens to everyone after a restart, the keys volume. |
| Verification or reset link says "This link can't be used" | Used or expired, the person reloaded after `app.js` removed the token, or the link points at the wrong site | `VerifyEmail.razor`, `ResetPassword.razor`; API `Email__PublicBaseUrl` | `select * from email_verification_tokens`/`password_reset_tokens` for the user; check `Email__PublicBaseUrl` is the web console's address. |
| Invitation link works, then "can't be used" after signing in | The `invitation_pending` cookie (1 hour) expired or could not be read (keys changed) | `Security/PendingInvitation.cs` | Ask for the link again; check the keys volume and the time taken. |
| Platform console says "You don't have access" to a real admin | `users.is_platform_admin` is false, or the probe was refused | `PlatformSession.OpenAsync` | `select email, is_platform_admin from users where email = '<address>';` |
| Platform console says "We couldn't open the platform console" | The probe `GET /api/auth/admin/webhooks?limit=1` failed (5xx, 429, unreachable) | `PlatformSession.ProbePath`; `PlatformLayout.razor` | API health and logs; the status the API returns for that route. |
| A role or access change does not show for the person | Their console token is cached until about a minute before expiry | `ConsoleTokenCache.Store`; `ConsoleSession.OpenAsync` | Wait up to about 14 minutes, or have them open `/choose-organisation` (it drops cached tokens). |
| An audit row reads "Launch url set" instead of a sentence | `AuditLabels` has no label for `auth.application.launch_url_set` | `Api/AuditContracts.cs` `AuditLabels` | Add the label. The action exists in `AuditActions.ApplicationLaunchUrlSet`. |

## Highest-risk areas in this part

1. **Everything stateful is in the web server's memory.** Sessions, console tokens and one-time secrets vanish on a restart, and a second
   copy would break sign-in. A secret lost after the API created the key cannot be recovered. Review `ISessionStore`,
   `OneTimeSecrets`, and the plan for scaling out (decision 0006 follow-up).
2. **The possible login redirect loop** when the API rejects a token the web session still thinks is fresh (clock difference or signing-key
   rotation). It would lock an affected person out of every page until the token renews. Reproduce and, if real, make a 401 on an
   identity token end the session or make `Login` check the session before redirecting. Review `ConsoleSession.OpenAsync`,
   `ChooseOrganisation.razor`, `PlatformSession.OpenAsync`, `Login.razor` `OnInitialized`.
3. **The token hand-off passes through the browser.** A nonce `script-src` now blocks injected scripts (AUTH-271), but the hand-off page
   still carries a live 15-minute product token in a form. Review `SecurityHeaders.cs`, `ProductEndpoints.HandOffPage`,
   and the single-use code alternative in decision 0007.
4. **Proxy and forwarded-address trust on both apps.** A wrong setting either puts every visitor in one rate-limit budget or lets an
   address be chosen by a visitor. Review `ProxyOptions`, `ClientAddressHandler`, the compose environment, and the API's `Auth:Proxy`.
5. **Silent failures and drift.** The web app logs nothing when the API fails, `GetAllAsync` truncates at 10,000 rows without a signal,
   `AuditLabels` is a hand-kept copy, the Owner role is matched by name, and page code strings must match the API's error codes exactly.
   Review `AuthApiClient.SendAsync` (add logging), `AuditContracts.cs`, and the codes used in `ConsoleEndpoints`/`PlatformEndpoints`.
6. **Decision 0002, README and `Home.razor` are out of date** against the code (statically rendered pages, finished screens, a "being
   built" placeholder at `/`). They mislead a new maintainer and on-call engineer.

## Unknowns and assumptions to confirm

- **Assumption (confirm):** the `Reference` code on error pages is the last 8 characters of Kestrel's `TraceIdentifier`
  (`<connection id>:<request number in hex>`), so it is not unique and is not logged by the web app. (How the console talks to the API.)
- **Unknown:** whether the API logs or uses the incoming W3C `traceparent` header to join web and API requests. Settle by reading the API's
  logging setup in `src/AuthService.Api/Observability`.
- **Assumption (confirm):** a 401 on the identity token while the web session still looks fresh causes a redirect loop between a page and
  `/login`, because `Login.razor` redirects signed-in visitors on. Triggers: clock difference, signing-key rotation. Settle by reproducing
  with a fake API. (The signed-in session.)
- **Assumption (confirm):** the 24-hour (verification) and 1-hour (reset) link lifetimes quoted on the pages match the API. They are
  text in `Signup.razor`, `VerifyEmail.razor` and `ForgotPassword.razor`, not read from the API. (Public pages.)
- **Assumption (confirm):** some browsers apply `form-action` to a redirect that follows the hand-off post, so a product redirecting to a
  different origin could be blocked. Settle by testing in the target browsers. (Picker and hand-off.)
- **Assumption (confirm):** the "about 15 minutes" wording on console pages equals `Auth:Tokens:ProductTokenMinutes`. The text is fixed in
  the pages. (The tenant console.)
- **Unknown:** whether the API's webhook list endpoint (the platform probe) is audited. The code comment says it is not. Settle by reading
  `src/AuthService.Api/Features/Webhooks/WebhookAdminEndpoints.cs`. (The platform console.)
- **Assumption (confirm):** a failed anti-forgery check may show the "Page not found" page with a 400 status because the empty 400 is
  re-executed by `UseStatusCodePagesWithReExecute("/not-found")`. I did not run it. (Security headers, anti-forgery ...)
- **Open question:** whether the signed-in "Existing" view of the invitation page should also remove `?token=` from the address bar.
  (Public pages.)
