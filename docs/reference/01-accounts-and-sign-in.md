# Accounts and sign-in

> Part 1 of the Auth Service reference. Overview, glossary and symptom index: [README](README.md).

This part covers how a person gets an account, proves their email address, signs in, stays signed in, signs out and recovers a
forgotten password. It also covers the identity access token that those flows produce. The web pages that front these endpoints
(`/signup`, `/verify-email`, `/login`, `/forgot-password`, `/reset-password`) are described in [part 6](06-web-console.md); only the
API contract is described here. How the emails are queued, sent and retried is described in part 5 (email delivery, see the
[README](README.md) for the part list). Choosing a tenant and obtaining a product token is token exchange, which is a separate part.

Terms used in this part:

- **Identity**: one global person record (`users` table), one email and one password for the whole platform (ADR
  `docs/decisions/0004-global-identity-with-tenant-memberships.md`). The tenant is not chosen at login.
- **Identity access token**: a short signed token that names the person only (`sub`). It has no tenant, no permissions and no audience.
- **Refresh token**: a long-lived opaque secret that is exchanged for a new identity access token. Only its hash is stored.
- **Family**: all refresh tokens that descend from one login (one device or browser session).

## Contents

1. [Sign-up](#sign-up)
2. [Email verification](#email-verification)
3. [Login](#login)
4. [Login protection](#login-protection)
5. [Refresh tokens](#refresh-tokens)
6. [Logout](#logout)
7. [Current user and current tenant](#current-user-and-current-tenant)
8. [Password reset](#password-reset)
9. [Password policy, email normalisation, secrets and one-time tokens](#password-policy-email-normalisation-secrets-and-one-time-tokens)
10. [The identity access token](#the-identity-access-token)
11. [Symptom to where to look (this part)](#symptom-to-where-to-look-this-part)
12. [Highest-risk areas in this part](#highest-risk-areas-in-this-part)
13. [Unknowns and assumptions to confirm](#unknowns-and-assumptions-to-confirm)

### Conventions used below

- Errors are returned by `ApiProblem.Create(status, code, title, detail)` in `src/AuthService.Api/Common/ApiProblem.cs`. The body is a
  standard problem document with an extra `code` field. Clients should branch on `code`, not on the text.
- Missing or invalid fields are returned as `Results.ValidationProblem` (HTTP 400) with an `errors` dictionary keyed by the request
  field name in Pascal case (for example `Email`, `NewPassword`). These 400 responses carry no `code`.
- JSON property names are camelCase in responses and enum values are written by name (`"Active"`), set in `Program.cs`
  (`JsonStringEnumConverter`).
- All defaults below live in the options classes, not in `appsettings.json` (which holds only logging settings).
  Settings are bound with `Configure<...>(GetSection(...))` in `Program.cs` and are **not validated at startup** (only `Auth:Proxy`,
  `Auth:RateLimit` and `Observability` are). A nonsense value such as `Auth:LoginProtection:MaxFailedAttempts=0` is accepted.

---

## Sign-up

### What it does

A new customer creates an account and their first organisation (tenant) in one step. They give an organisation name, an email address,
a password and, optionally, a display name. The service creates the person, the organisation, their membership of it and a built-in
Owner role. It then emails a verification link. The person cannot log in until the address is verified (see [Email verification](#email-verification)).

Rules:

- All four creations succeed or none does. The user, tenant, membership, Owner role, role assignment, verification token, queued email
  and audit entry are saved by one `SaveChangesAsync`, so a failure part-way leaves nothing behind (test `AUTH_TC3_FailureDuringSignupLeavesNoOrphanedRows`).
- One email address can have one account on the whole platform, compared without regard to case or surrounding spaces. A second
  signup with the same address returns 409 and tells the person to log in and create a tenant instead. Accounts are never merged.
- Organisation name: required, trimmed, at most 200 characters (`SignupValidator.MaxTenantNameLength`). Organisation names are not
  unique.
- Email: required, normalised (see section 9), at most 254 characters (`MaxEmailLength`), must parse as an address and its domain
  must contain a dot (`SignupValidator.IsPlausibleEmail`). So `owner@acme` is rejected.
- Password: 8 to 128 characters (`PasswordPolicy`, section 9).
- Display name: optional, trimmed, at most 200 characters.
- The new tenant is `Active`, the membership is `Active`, and the role is the built-in `Owner`.
- The person starts with `users.email_verified_at` null.
- Signup is anonymous and has **no rate limit**.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/signup` | None (`AllowAnonymous`) | `SignupEndpoints.SignUpAsync` in `src/AuthService.Api/Features/Signup/SignupEndpoints.cs` | `SignupService.SignUpAsync` | Validates, creates user, tenant, membership, Owner role, verification token and email; returns 201 with `tenantId`, `userId`, `membershipId`, `email`. |

Flow:

1. `SignupEndpoints.SignUpAsync` calls `SignupValidator.TryValidate`. Any problem returns 400 (`Results.ValidationProblem`) and nothing is written.
2. `SignupService.SignUpAsync` runs a friendly pre-check: `db.Users.AnyAsync(user => user.Email == signup.Email)`. If found it returns `SignupResult.EmailAlreadyRegistered`.
3. It builds the `User` (id from `Guid.CreateVersion7()`, `CreatedAt` from `TimeProvider`) and hashes the password with `IPasswordHasher<User>`.
4. `db.AddTenantWithOwner(...)` (`src/AuthService.Api/Features/Tenants/TenantProvisioning.cs`, shared with "create another tenant") adds the `Tenant`, `TenantMembership`, `Role` (name `Owner`, `IsBuiltIn`) and `MembershipRole`.
5. `EmailVerificationService.Issue` adds the verification token and enqueues the `EmailTemplate.Verification` email in `email_outbox`.
6. `audit.Record(AuditActions.SignupCompleted, AuditActor.User(user.Id), tenantId)`.
7. `SaveChangesAsync`. If it throws a `DbUpdateException` that is a PostgreSQL unique violation on the index named `UserConfiguration.UniqueEmailIndexName` (`ux_users_email`), the result is again `EmailAlreadyRegistered`. This is what makes two simultaneous signups with the same address safe (test `TEN01_Concurrency_SimultaneousSignupsWithTheSameEmailCreateExactlyOneAccount`).
8. The endpoint maps the result: 201 `Created`, or 409 with code `email_already_registered`.

Data (entities in `src/AuthService.Infrastructure/Persistence/Entities`):

| Table | Entity | Written by signup |
|---|---|---|
| `users` | `User` | id, `email` (citext, unique index `ux_users_email`), `password_hash`, `display_name`, `created_at`. `email_verified_at`, `failed_login_count`, `locked_until` start empty or zero; `is_platform_admin` defaults false. |
| `tenants` | `Tenant` | name, status `Active`. |
| `tenant_memberships` | `TenantMembership` | status `Active`. |
| `roles`, `membership_roles` | `Role`, `MembershipRole` | the built-in Owner role and its assignment. |
| `email_verification_tokens` | `EmailVerificationToken` | one row (hash only). |
| `email_outbox` | `EmailOutboxMessage` | one `Verification` message; the raw token sits in `payload_json` until the email is sent or given up. |
| `audit_entries` | `AuditEntry` | `auth.signup.completed` (`AuditActions.SignupCompleted`), actor `User`, with the new tenant id. |

Libraries: ASP.NET Core Identity `PasswordHasher<User>` (password hashing), EF Core with Npgsql (the unique-violation check uses `PostgresException`, `PostgresErrorCodes.UniqueViolation`), `System.Net.Mail.MailAddress` (plausibility check).

Background workers: `EmailDispatcher` sends the verification email. If it is not running (`Email:DispatcherEnabled=false`) or the provider fails, the account exists but the person never receives the link. They can use the resend endpoint. See part 5.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Email:PublicBaseUrl` (`Email__PublicBaseUrl`) | `http://localhost:5080` | `src/AuthService.Api/Email/EmailOptions.cs` | The verification link points here. **Must be set in production** (listed as required in `docs/deployment.md`); the default sends people to localhost. |
| `Email:Provider` (`Email__Provider`) | unset (File in Development, InMemory in Testing) | `EmailOptions` | Required outside Development and Testing, or the service refuses to start. Decides whether the link email is delivered. |
| `Auth:EmailVerification:TokenHours` (`Auth__EmailVerification__TokenHours`) | 24 | `EmailVerificationOptions` | Lifetime of the link issued at signup. |
| `ConnectionStrings:AuthDb` (`ConnectionStrings__AuthDb`) | none | `Program.cs` | Required; the service throws "Connection string 'AuthDb' is not configured." on first use. |

The password and field limits are constants (`PasswordPolicy`, `SignupValidator`), not settings.

### Failure modes and production debugging

What the caller sees:

| Situation | Status | `code` |
|---|---|---|
| Missing or invalid organisation name, email, password or display name | 400 | none (`errors` keys `TenantName`, `Email`, `Password`, `DisplayName`) |
| Email already registered (including a different case or surrounding spaces) | 409 | `email_already_registered` |
| Database failure part-way | 500 | none (plain problem response from `AddProblemDetails`); everything rolls back |

Logged and audited:

- Audit action `auth.signup.completed` (actor type `User`, with `tenant_id`). Nothing is audited for a refused signup.
- No application log line is written by signup itself. A 500 is logged by the exception handler.

Edge cases and half-states:

- **Unverified account holds its email address.** Someone who signed up, never received the email and tries again gets 409. They must use resend (section 2) or password reset (section 8), or log in after verifying.
- **Account exists but no email was ever sent** if the outbox row is `Failed`. See the SQL below.
- The 409 deliberately reveals that an address is registered. ADR 0004 records this as accepted for signup; password reset and resend do not reveal it.

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| Verification email never delivered (provider not configured, worker disabled, sender not verified) | `SELECT template, status, attempts, next_attempt_at, last_error, sent_at FROM email_outbox WHERE to_address = '<email>' ORDER BY created_at DESC;` Status `Pending` with growing `attempts` or `Failed` means delivery, not signup, is the problem. |
| Link in the email points at the wrong host | Compare `Email__PublicBaseUrl` with the host in the email. |
| Spam or bot signups | No signup limit exists. Count recent rows: `SELECT count(*) FROM audit_entries WHERE action = 'auth.signup.completed' AND occurred_at > now() - interval '1 hour';` and group by `source_ip`. |

### Decisions and open questions

- Signup reuses `TenantProvisioning.AddTenantWithOwner`, so signup and "create another tenant" (TEN-09) can never create tenants differently.
- Unique email is enforced twice: by lowercasing in code and by the `citext` column with a unique index.
- **Unprotected endpoint:** signup has no per-IP limit and no CAPTCHA, and each call performs a password hash and queues an email. This could be abused to send verification emails to arbitrary addresses (one per signup, each a new account) and to fill `users`. Review whether PLT-04 rate limiting should cover it.
- **Assumption (confirm):** there is no email-domain allow-list or disposable-address check. None exists in the code read.
- Organisation names are not unique, so support cannot find a customer by name alone.

---

## Email verification

### What it does

The service proves that a new person controls the email address they signed up with. After signup they receive a link. Opening it
marks the address verified, and only then can they log in. If the link expires or is lost they ask for a new one.

Rules:

- A verification link is valid for **24 hours** (`Auth:EmailVerification:TokenHours`) and works once.
- Opening a link for an address that is already verified is harmless and answers 200, even if that link has expired or been used.
- Opening a valid link also retires every other unused link for the same person.
- Asking for a new link always answers 202 with the same message, whether or not the address is registered or verified (it cannot be used to find out which addresses exist).
- A new link is **not** sent if any verification token for that person was created less than **60 seconds** ago (`ResendCooldownSeconds`). The cooldown is silent: the answer is still 202. Signup's own token counts.
- Older unused links stay valid until they expire, so a delayed first email still works.
- One IP address may call resend **10 times per 10 minutes** (`ResendIpLimit`, `ResendIpWindowMinutes`), then gets 429.
- The verify endpoint itself has no rate limit (the token is 256 bits of randomness, so guessing is not practical).

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/email/verify` | None | `EmailVerificationEndpoints.VerifyAsync` (`src/AuthService.Api/Features/EmailVerification/EmailVerificationEndpoints.cs`) | `EmailVerificationService.VerifyAsync` | Consumes the token and sets `users.email_verified_at`. Body `{ "token": "..." }`. 200 `{ "verified": true }`. |
| `POST /api/auth/email/verify/resend` | None; shared rate limit policy `email-verification-resend` | `EmailVerificationEndpoints.ResendAsync` | `EmailVerificationService.ResendAsync` | Issues a new token and queues an email if the account exists, is unverified and is outside the cooldown. Body `{ "email": "..." }`. Always 202. |

Verify flow (`EmailVerificationService.VerifyAsync`):

1. Hash the presented token with `SecretToken.Hash` and look it up by `token_hash`. Unknown gives `InvalidOrExpired`.
2. If the owning user is already verified, return `AlreadyVerified` (mapped to 200).
3. If `used_at` is set or `expires_at <= now`, return `InvalidOrExpired`.
4. Open a transaction. Claim the token with one conditional `UPDATE ... WHERE used_at IS NULL AND expires_at > now`. If zero rows changed (a simultaneous open of the same link), roll back and answer `AlreadyVerified` if the user is now verified, otherwise `InvalidOrExpired`.
5. Set `users.email_verified_at = now` where it was null. Only if a row changed, record `AuditActions.EmailVerified` and `SaveChangesAsync`.
6. Mark all the person's other unused tokens used. Commit. Result `Verified`.

Resend flow (`EmailVerificationService.ResendAsync`): normalised email from `EmailNormalizer.Normalize`; find the user; return silently if none or already verified; return silently if any token has `created_at` newer than now minus the cooldown; otherwise `Issue(...)` and save (token row and outbox row in one transaction).

Data:

| Table | Entity | Columns |
|---|---|---|
| `email_verification_tokens` | `EmailVerificationToken` | `id`, `user_id` (FK to `users`, restrict), `token_hash` (64 chars, unique index), `created_at`, `expires_at`, `used_at`. Index on (`user_id`, `created_at`) serves the cooldown query. |
| `users` | `User` | `email_verified_at`. |
| `email_outbox` | `EmailOutboxMessage` | template `Verification`, payload holds the raw token until sent. |

The email (`src/AuthService.Api/Email/EmailTemplates.cs`, template `EmailTemplate.Verification`) has the subject "Verify your email address" and a link of the form `{Email:PublicBaseUrl}/verify-email?token=<token>`. The page at that address posts the token to the verify endpoint (part 6).

Tokens are never purged. There is no cleanup job for `email_verification_tokens`.

Background workers: `EmailDispatcher` (interval `Email:DispatchIntervalMilliseconds`, default 2000) sends the queued email. If it is not running, links are created but never delivered. `RateLimitCleanupWorker` removes expired rate counters (resend limit only).

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:EmailVerification:TokenHours` (`Auth__EmailVerification__TokenHours`) | 24 | `EmailVerificationOptions` | Link lifetime. The email states this value (`EmailLifetime.Describe`). |
| `Auth:EmailVerification:ResendCooldownSeconds` (`Auth__EmailVerification__ResendCooldownSeconds`) | 60 | `EmailVerificationOptions` | Minimum gap between links for one account. 0 disables it. |
| `Auth:EmailVerification:ResendIpLimit` (`Auth__EmailVerification__ResendIpLimit`) | 10 | `EmailVerificationOptions` | Resend requests per IP per window before 429. |
| `Auth:EmailVerification:ResendIpWindowMinutes` (`Auth__EmailVerification__ResendIpWindowMinutes`) | 10 | `EmailVerificationOptions` | Window length. |
| `Email:PublicBaseUrl` (`Email__PublicBaseUrl`) | `http://localhost:5080` | `EmailOptions` | Host in the link. |
| `Auth:Proxy:TrustedProxies` / `TrustedNetworks` / `ForwardLimit` (`Auth__Proxy__TrustedProxies__0` etc.) | empty; 1 | `src/AuthService.Api/Common/ProxyOptions.cs` | Without it the per-IP resend limit sees the proxy's address for everyone. |
| `Auth:RateLimit:Store` (`Auth__RateLimit__Store`) | `Postgres` | `src/AuthService.Api/RateLimiting/RateLimitOptions.cs` | `Memory` keeps counters per process; with several instances the limit multiplies. |

### Failure modes and production debugging

What the caller sees:

| Situation | Status | `code` |
|---|---|---|
| Missing `token` or `email` | 400 | none (`errors` key `Token` or `Email`) |
| Unknown, expired or used link (address still unverified) | 410 | `verification_link_invalid` ("This verification link is invalid or has expired. Request a new one.") |
| Link for an already verified address | 200 | none; `{ "verified": true }` |
| Resend, any email, including cooldown suppression | 202 | none; "If that email belongs to an unverified account, a new verification link has been sent." |
| Resend over the IP limit | 429 | `rate_limited`, with `Retry-After` |
| Rate-limit store unreachable | 503 | `rate_limit_unavailable`, `Retry-After: 5` |

Logged and audited:

- Audit action `auth.email.verified` (`AuditActions.EmailVerified`), actor `User`, written in the same transaction as the change.
- Nothing is audited or logged for resend or for a refused verify.
- When the rate-limit store fails: error log "The rate-limit store could not be reached, so a request to a limited endpoint was refused ({Policy})."

Edge cases and half-states:

- **Resend does nothing silently** inside 60 seconds of any earlier token for that person. If the first email failed to send (outbox `Failed`), a resend within the cooldown still answers 202 and sends nothing; after the cooldown it works.
- A person whose link has expired has an unverified account and must resend. Nothing resends automatically.
- A person verified through another route (accepting an invitation sets `email_verified_at`, in `AcceptInvitationService`) keeps unused verification tokens; harmless.
- A successful password reset also marks the email verified (section 8), because following the emailed link proves the person controls the inbox.
- The token travels in the URL query string of the web console page. Access logs of the console host or its proxy will contain it. It is single-use and expires, but treat those logs as sensitive.

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| Link older than 24 hours | `SELECT t.created_at, t.expires_at, t.used_at FROM email_verification_tokens t JOIN users u ON u.id = t.user_id WHERE u.email = '<email>' ORDER BY t.created_at DESC;` `expires_at` in the past and `email_verified_at` null on the user. |
| Link mangled by the mail client (line wrap, truncated query) | The caller gets 410; compare the length of the `token` value: it should be 43 characters (base64url of 32 bytes). |
| Link generated by another environment (different database) | The token hash is not found. Check `Email__PublicBaseUrl` and which API the console talks to. |
| Mail not arriving | `email_outbox` query in the Sign-up section. |
| Everyone behind one office address gets 429 on resend | Proxy not listed in `Auth:Proxy`; `audit_entries.source_ip` shows the proxy's address. |

### Decisions and open questions

- The same 410 is returned for unknown, expired and used links, so it reveals nothing about which it was.
- Verification is idempotent for verified people, so a person who double-clicks or whose mail scanner opens the link is not shown an error.
- Old links stay valid after a resend (a delayed first email still works), at the cost of several live tokens per person.
- **Unfinished:** no purge of old rows in `email_verification_tokens`.
- **Assumption (confirm):** an unverified account is meant to stay in the table indefinitely, holding its address. No expiry of unverified accounts exists in the code read.

---

## Login

### What it does

A person signs in with their email address and password. If they are right, the service returns a short-lived identity access
token, a long-lived refresh token and the list of organisations the person belongs to, so the client can ask which one to work in.
Login does not choose an organisation.

Rules:

- Email is matched after trimming and lowercasing; the password is compared exactly (never trimmed).
- The person must have a password, a verified email, and at least one non-disabled membership (or none at all).
- A wrong password, an unknown email, an account with no password yet and a locked account all give the **same** 401 `invalid_credentials`. The answer takes about the same time in each case because an unknown or locked account still performs one password hash check (`DummyHash`).
- Only after the password is correct does the service tell the person why they cannot log in: 403 `account_disabled` or 403 `email_not_verified`.
- "Disabled" means the person has at least one membership and **all** of them are `Disabled` (`AccountAccess.IsDisabled`). One disabled membership among active ones does not block login.
- A person with no memberships at all is not "disabled" and can log in.
- The access token lasts **15 minutes** (`expiresIn` is 900), the refresh token **30 days**.
- Every response from the endpoint is sent with `Cache-Control: no-store` (set after validation passes).
- The endpoint is limited to **20 requests per IP per minute** (see section 4).

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/login` | None; shared rate limit policy `login` | `LoginEndpoints.LoginAsync` (`src/AuthService.Api/Features/Login/LoginEndpoints.cs`) | `LoginService.LoginAsync` | Checks credentials and issues tokens. Body `{ "email", "password" }`. 200 `{ accessToken, tokenType: "Bearer", expiresIn, refreshToken, memberships: [{ tenantId, tenantName, status }] }`. |

Flow (`LoginService.LoginAsync`):

1. The endpoint rejects a blank email or an empty password with 400, then calls `EmailNormalizer.Normalize` on the email.
2. Look up the user by `email`. If there is no user, or `password_hash` is null: run `VerifyHashedPassword` against `DummyHash`, record `auth.login.failed` with detail `unknown_account` (actor `Anonymous`, the email is not recorded) and return `InvalidCredentials`.
3. If `locked_until > now`: run the dummy hash check, record `auth.login.failed` with detail `account_locked` and return `InvalidCredentials`. The attempt is not counted.
4. Verify the password with `IPasswordHasher<User>`. On failure call `RegisterFailureAsync` (section 4) and return `InvalidCredentials`.
5. On success, if `failed_login_count > 0` or `locked_until` is set, reset them to 0 and null with one `UPDATE`.
6. Load memberships with `db.ListMembershipsAsync` (`src/AuthService.Api/Features/Accounts/MembershipSummary.cs`: all statuses, ordered by tenant name then id).
7. If `AccountAccess.IsDisabled(...)`: audit `auth.login.failed` detail `account_disabled`, return `AccountDisabled` (403).
8. If `email_verified_at` is null: audit `auth.login.failed` detail `email_not_verified`, return `EmailNotVerified` (403).
9. `IdentityTokenService.CreateAccessToken(user.Id, now)` and `CreateRefreshToken(user.Id, now)` (new family). Add the refresh token row, record `auth.login.succeeded`, `SaveChangesAsync`.

Data:

| Table | Entity | Use |
|---|---|---|
| `users` | `User` | Read `password_hash`, `email_verified_at`, `locked_until`; write `failed_login_count`, `locked_until`. |
| `tenant_memberships`, `tenants` | `TenantMembership`, `Tenant` | Read for the membership list and the disabled check. |
| `refresh_tokens` | `RefreshToken` | New row with a new `family_id`. |
| `audit_entries` | `AuditEntry` | `auth.login.succeeded`, `auth.login.failed`. |

Libraries: ASP.NET Core Identity `PasswordHasher<User>`; `Microsoft.IdentityModel.JsonWebTokens` for the access token (section 10).

Background workers: none for the request itself. The signing key worker (`SigningKeyWorker`) must have loaded a key at startup, or no token can be issued.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:AccessTokenMinutes` (`Auth__Tokens__AccessTokenMinutes`) | 15 | `src/AuthService.Api/Tokens/IdentityTokenOptions.cs` | Access token life and `expiresIn`. |
| `Auth:Tokens:RefreshTokenDays` (`Auth__Tokens__RefreshTokenDays`) | 30 | `IdentityTokenOptions` | Refresh token life. |
| `Auth:Tokens:Issuer` (`Auth__Tokens__Issuer`) | `AuthService` | `IdentityTokenOptions` | `iss` of every token. Must match what products expect. |
| `Auth:Tokens:KeyEncryptionKey` (`Auth__Tokens__KeyEncryptionKey`) | none | `IdentityTokenOptions`, used by `KeyEncryption` | **Required outside Development and Testing**; the service will not start without it. Losing it loses all stored signing keys. |
| `Auth:LoginProtection:*` | see section 4 | `LoginProtectionOptions` | Lockout and per-IP limit. |

### Failure modes and production debugging

What the caller sees:

| Situation | Status | `code` |
|---|---|---|
| Blank email or empty password | 400 | none (`errors` keys `Email`, `Password`) |
| Wrong password, unknown email, no password set, locked account | 401 | `invalid_credentials` ("The email or password is incorrect.") |
| Right password, every membership disabled | 403 | `account_disabled` |
| Right password, email not verified | 403 | `email_not_verified` |
| Over the per-IP limit | 429 | `rate_limited`, `Retry-After` set |
| Rate-limit store unreachable | 503 | `rate_limit_unavailable` |

Logged, audited and counted:

- Audit `auth.login.succeeded`; `auth.login.failed` with `detail` one of `wrong_password`, `unknown_account`, `account_locked`, `account_disabled`, `email_not_verified`. `auth.account.locked` when a lock starts.
- Metric `auth_login_failures_total{reason="..."}` with the same five reason words (`AuthMetrics.RecordLoginFailure`).
- No log line for an ordinary failure. The lockout writes a warning (section 4).

Edge cases and half-states:

- A person invited but who never accepted has a `users` row with null `password_hash`. Login returns the generic 401 and an `unknown_account` audit entry with no actor.
- Counts are not kept for unknown emails (`TEN12_FailuresForAnUnknownEmailAreNotCountedAnywhere`), so an attacker cannot lock an address that does not exist, but can lock a real one (section 4).
- Failed attempts write one `audit_entries` row each, so a flood of failures grows that table until `Auth:Audit` retention removes rows (365 days by default).
- A correct password with an unverified email still clears the failed-attempt count and any lock, before the 403 is returned.
- The memberships list includes `Invited` and `Disabled` memberships. The client decides what to show.
- Login does not apply the 128-character password limit; only the request body size limit bounds the length hashed.

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| "Invalid credentials" for a correct password because the account is locked | `SELECT email, failed_login_count, locked_until FROM users WHERE email = '<email>';` and `SELECT occurred_at, action, detail FROM audit_entries WHERE actor_id = '<user id>' ORDER BY occurred_at DESC LIMIT 20;` Look for `account_locked`. |
| Person never set a password (invitation not accepted) | `password_hash IS NULL` on the user; audit detail `unknown_account` with the user's id absent (actor is anonymous). |
| Wrong environment or restored database | Compare `users` with expectations; check which `ConnectionStrings__AuthDb` the instance uses. |
| 403 `email_not_verified` after a reset | Only if the reset was done before AUTH-266 was deployed: `email_verified_at` is null; the person needs resend (section 2). |
| 403 `account_disabled` | `SELECT t.name, m.status FROM tenant_memberships m JOIN tenants t ON t.id = m.tenant_id WHERE m.user_id = '<user id>';` All `Disabled`. |
| Many 429s | Section 4. |
| 500 on login right after a deploy | Migrations not applied (they are never applied on startup, ADR 0003). Check the application log for a missing-column error. |

### Decisions and open questions

- Ordering of checks (password first, then disabled, then verified) is intentional: telling the person why they cannot log in only after a right password leaks nothing to a guesser (code comment in `LoginService`).
- Tokens are issued with the global identity only. The tenant is chosen at token exchange (ADR 0004).
- The unknown-account audit row has no actor and no email, so support cannot tell which address was tried. That is deliberate privacy but limits investigation.
- **Risk:** a stored password hash is never upgraded after a successful login, even if the library reports it is out of date (see section 9).
- **Assumption (confirm):** a person with zero memberships is allowed to log in (they get an empty list). The code treats "disabled" as requiring at least one membership; whether a zero-membership person can exist in practice is not shown (invitation acceptance and signup always create one).

---

## Login protection

### What it does

Two protections stop password guessing. First, an account that gets five wrong passwords in a row is locked for 15 minutes. Second,
one IP address may make only 20 login requests per minute across all accounts. Both give the person no hint beyond a generic error.

Rules:

- **Per account.** Each wrong password for a real account adds one to `users.failed_login_count`. The fifth in a row locks the account (`MaxFailedAttempts` 5) for 15 minutes (`LockoutMinutes`), sets `locked_until`, and resets the count to 0.
- A locked account is refused with the same 401 `invalid_credentials`, even with the right password. Attempts during a lock are not counted and do not extend it.
- A right password clears the count and any lock (including one that has run out).
- A password reset clears the count and lock (section 8).
- Wrong passwords for an unknown email are not counted anywhere.
- The lock is per account, not per address: other accounts are unaffected (`TEN12_TheLockoutIsPerAccount`).
- **Per IP address.** 20 requests per fixed 1-minute window (`IpLimit`, `IpWindowMinutes`) across all accounts, successful or not. Over the limit gives 429 `rate_limited` with `Retry-After`.
- The windows are fixed windows: the first request starts the window and the count restarts when it ends.
- If the counter store (PostgreSQL by default) is unreachable, the login endpoint answers 503 `rate_limit_unavailable`. It never lets the request through unlimited (decision 0008).

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/login` | None | `LoginEndpoints.LoginAsync` | `LoginService.RegisterFailureAsync` | Counts a wrong password and locks on the limit. |
| (middleware, every limited endpoint) | n/a | `SharedRateLimitMiddleware.InvokeAsync` (`src/AuthService.Api/RateLimiting/SharedRateLimit.cs`) | `RateLimitPolicies.Resolve`, `IRateLimitStore.HitAsync` | Counts the request under a key made of the policy name `login` and the client IP address, and answers 429 or 503 before the handler runs. |

Per-account flow (`LoginService.RegisterFailureAsync`):

1. One `UPDATE users` sets `failed_login_count` to `0` if `failed_login_count + 1 >= MaxFailedAttempts`, otherwise `+ 1`, and in the same statement sets `locked_until` to `now + LockoutMinutes` when the limit is reached. Doing both in SQL means simultaneous wrong passwords cannot be miscounted.
2. Read `locked_until` back. Because PostgreSQL stores microseconds and .NET keeps finer ticks, the code compares with a tolerance of 1 ms to decide whether this call started a lock.
3. Always record `auth.login.failed` (detail `wrong_password`) and count the metric. If a lock started, also record `auth.account.locked` (detail `too_many_failed_logins`) and log the warning below.
4. `SaveChangesAsync`.

Per-IP flow: `SharedRateLimitMiddleware` runs after authentication and before authorization (`Program.cs`). It reads the endpoint's `SharedRateLimitMetadata`, builds the key from the policy name and `HttpContext.Connection.RemoteIpAddress`, and calls `PostgresRateLimitStore.HitAsync`, which does one `INSERT ... ON CONFLICT DO UPDATE` on `rate_limit_counters` (column `key_hash` is a SHA-256 of the key, so the address is not stored).

Data: `users.failed_login_count`, `users.locked_until`; `rate_limit_counters` (`key_hash`, `hits`, `expires_at`); `audit_entries`.

Background workers: `RateLimitCleanupWorker` deletes counters whose window ended more than a minute ago, every `Auth:RateLimit:CleanupSeconds` (60). If it is not running the table grows but limits stay correct (an ended window is replaced on use).

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:LoginProtection:MaxFailedAttempts` (`Auth__LoginProtection__MaxFailedAttempts`) | 5 | `src/AuthService.Api/Features/Login/LoginProtectionOptions.cs` | Wrong passwords in a row before a lock. 0 or negative would lock on the first wrong password (no validation). |
| `Auth:LoginProtection:LockoutMinutes` (`Auth__LoginProtection__LockoutMinutes`) | 15 | `LoginProtectionOptions` | Lock length. |
| `Auth:LoginProtection:IpLimit` (`Auth__LoginProtection__IpLimit`) | 20 | `LoginProtectionOptions` | Login requests per IP per window. |
| `Auth:LoginProtection:IpWindowMinutes` (`Auth__LoginProtection__IpWindowMinutes`) | 1 | `LoginProtectionOptions` | Window length. |
| `Auth:RateLimit:Store` (`Auth__RateLimit__Store`) | `Postgres` | `RateLimitOptions` | `Memory` makes counts per process: with N instances the effective limit is N times higher and a restart clears it. Any other value stops startup. |
| `Auth:RateLimit:CleanupSeconds` (`Auth__RateLimit__CleanupSeconds`) | 60 | `RateLimitOptions` | How often expired counters are purged. Below 1 stops startup. |
| `Auth:Proxy:TrustedProxies` / `TrustedNetworks` (`Auth__Proxy__TrustedProxies__0`, `Auth__Proxy__TrustedNetworks__0`) and `Auth:Proxy:ForwardLimit` (`Auth__Proxy__ForwardLimit`) | empty; 1 (allowed 1 to 10) | `src/AuthService.Api/Common/ProxyOptions.cs` | **Critical when behind a proxy or load balancer.** Only from a listed proxy is `X-Forwarded-For` believed. Unlisted, every user shares the proxy's address and the 20-per-minute budget. A typo stops startup. |

### Failure modes and production debugging

What the caller sees: see the Login table. For this feature: 401 `invalid_credentials` (identical for wrong password and lock), 429 `rate_limited` with a `Retry-After` header in seconds (rounded up from the window's remaining time, minimum 1), 503 `rate_limit_unavailable` with `Retry-After: 5`.

Logged and audited:

- Warning log: "Account {UserId} locked for {LockoutMinutes} minutes after {MaxFailedAttempts} failed logins." (user id only, never the email).
- Error log on store failure: "The rate-limit store could not be reached, so a request to a limited endpoint was refused ({Policy})."
- Audit: `auth.login.failed` (detail `wrong_password` or `account_locked`), `auth.account.locked` (detail `too_many_failed_logins`).
- Metric: `auth_login_failures_total{reason="wrong_password"}` and `{reason="account_locked"}`. 429s appear in `auth_http_requests_total` with the login route and status 429.

Edge cases and half-states:

- **Lock-out by a third party.** Anyone who knows an email can lock that account for 15 minutes with five wrong passwords, repeatedly. There is no CAPTCHA and no admin unlock endpoint. The columns `failed_login_count` and `locked_until` are only touched by `LoginService` and `PasswordResetService`.
- A lock does not end existing sessions. Refresh (section 5) does not check `locked_until`.
- The per-IP counter counts every request, including successful logins and requests that fail validation.
- The 429 and 503 come from the middleware before the handler, so they are not audited and not counted in `auth_login_failures_total`.
- The counter key is hashed, so you cannot look up "which IP is limited" in the table directly; you can see the hottest counters (below) and compare with `audit_entries.source_ip`.

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| A user is locked out | `SELECT id, email, failed_login_count, locked_until FROM users WHERE locked_until > now();` and the audit query for `auth.account.locked`. |
| Many different users get 429 on login | `Auth__Proxy__*` not set while behind a load balancer: `SELECT source_ip, count(*) FROM audit_entries WHERE action LIKE 'auth.login.%' AND occurred_at > now() - interval '15 minutes' GROUP BY source_ip ORDER BY 2 DESC;` shows one address (the proxy's) for everyone. Also `SELECT hits, expires_at FROM rate_limit_counters WHERE expires_at > now() ORDER BY hits DESC LIMIT 5;` |
| Office or school behind one NAT address gets 429 | Same query: many `source_ip` entries are one address that really is shared. Raise `Auth__LoginProtection__IpLimit`. |
| Lockouts seem not to apply, or limits are too lax with several instances | `Auth__RateLimit__Store=Memory` in a multi-instance deployment. (Account lockout uses the database and is unaffected; only the IP limit is.) |
| 503 `rate_limit_unavailable` | The database is down or slow; check the error log line above and `/health/ready` (platform part). |
| Attack in progress | `auth_login_failures_total` rising for `wrong_password`; audit rows with `unknown_account` and varied `source_ip`. |

### Decisions and open questions

- Locking counts consecutive wrong passwords for real accounts only. Unknown emails are not counted, so the lock state is never created for addresses that do not exist and cannot be used to test existence.
- Counting is one atomic SQL statement; there is no read-modify-write race.
- A locked attempt still costs a password hash so response time does not reveal the lock.
- **Risk:** targeted lock-out (above) is a denial-of-service against any known customer email. The per-IP limit does not help against a distributed attacker. Review whether to add an unlock path for support and some per-account backoff other than a fixed 15 minutes.
- **Risk:** the per-IP limit protects nothing if `Auth:Proxy` is not set behind a proxy. It becomes a global limit of 20 logins per minute for the whole customer base.
- **Inconsistent:** the README still says the audit log "will record" the lockout; it now does (`auth.account.locked`).
- **Assumption (confirm):** the lock is not meant to revoke refresh tokens. The code never does so.

---

## Refresh tokens

### What it does

A signed-in person (or the web console on their behalf) trades a refresh token for a new 15-minute identity access token without
asking for the password again. Each refresh token works once: using it returns the next one. Using an old one again is treated as
theft and ends that whole login session.

Rules:

- A refresh token is 256 bits of randomness, shown once, stored only as a SHA-256 hash.
- Lifetime **30 days** (`Auth:Tokens:RefreshTokenDays`). The expiry of the new token is counted from the moment of rotation, so a session that is used at least once every 30 days never has to log in again. **There is no absolute maximum session length.**
- Rotation is always on: presenting a valid token revokes it and issues the next token in the same family (same `family_id`).
- Presenting a token that has already been revoked, whatever the reason, revokes the whole family and records `auth.refresh_token.reuse_detected`.
- Different logins (devices) are different families. A replay on one device does not end the others (`TEN03_ReplayOnOneLoginDoesNotEndTheSessionOnAnotherDevice`).
- Unknown, expired, revoked and replayed tokens all get the same 401 `invalid_refresh_token`.
- A person whose every membership is disabled gets 403 `account_disabled`, so removing someone's access also ends their ability to renew a session.
- Refresh does not check the email-verified flag or the lock (`locked_until`).
- Revocation: logout revokes the family (section 6); a password reset revokes every token of the person (section 8).
- The refresh endpoint is not rate limited.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/refresh` | None (the refresh token is the credential) | `RefreshEndpoints.RefreshAsync` (`src/AuthService.Api/Features/Refresh/RefreshEndpoints.cs`) | `RefreshService.RefreshAsync` | Rotates the token. Body `{ "refreshToken": "..." }`. 200 `{ accessToken, tokenType, expiresIn, refreshToken }` with `Cache-Control: no-store`. |

Flow (`RefreshService.RefreshAsync`):

1. Hash the token (`IdentityTokenService.Hash`, which calls `SecretToken.Hash`) and find the row by `token_hash`. None gives `InvalidRefreshToken`.
2. If `revoked_at` is set: `RevokeFamilyAsync` (`db.RevokeRefreshTokenFamilyAsync` in `src/AuthService.Api/Tokens/RefreshTokenExtensions.cs`), `RecordReuseAsync` (audit `auth.refresh_token.reuse_detected`, actor `User`), return `InvalidRefreshToken`.
3. If `expires_at <= now`: return `InvalidRefreshToken` (family is not revoked, nothing is audited).
4. Load the person's membership statuses. If `AccountAccess.IsDisabled(...)`: return `AccountDisabled`. The token is left untouched.
5. Begin a transaction and claim the token with one conditional `UPDATE ... SET revoked_at = now WHERE id = ... AND revoked_at IS NULL`. If zero rows changed (a concurrent request won), roll back, revoke the family, audit reuse, return `InvalidRefreshToken`.
6. `tokens.CreateRefreshToken(userId, familyId, now)`, add it, `SaveChangesAsync`, commit.
7. `tokens.CreateAccessToken(userId, now)` and return.

Data: `refresh_tokens` (entity `RefreshToken`): `id`, `user_id` (FK to `users`, restrict), `family_id` (indexed), `token_hash` (unique index), `created_at`, `expires_at`, `revoked_at`. States: live (`revoked_at` null and `expires_at` in the future), spent (revoked by rotation), revoked (logout, reuse, password reset), expired.

Libraries: EF Core `ExecuteUpdateAsync` for the atomic claim.

Background workers: none. **Nothing purges `refresh_tokens`**: the hosted services registered in `Program.cs` are for signing keys, email dispatch, webhook dispatch, audit retention and rate-limit counters, and none deletes expired or revoked token rows.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:RefreshTokenDays` (`Auth__Tokens__RefreshTokenDays`) | 30 | `IdentityTokenOptions` | Lifetime applied at login and at every rotation. Existing rows keep the expiry they were given. |
| `Auth:Tokens:AccessTokenMinutes` (`Auth__Tokens__AccessTokenMinutes`) | 15 | `IdentityTokenOptions` | Access token returned by refresh. |

There is no setting to turn rotation off or to cap the total session age.

### Failure modes and production debugging

What the caller sees:

| Situation | Status | `code` |
|---|---|---|
| Missing token | 400 | none (`errors` key `RefreshToken`) |
| Unknown, expired, revoked, replayed or lost-race token | 401 | `invalid_refresh_token` ("The refresh token is invalid, expired or has already been used.") |
| Every membership disabled | 403 | `account_disabled` |

Logged and audited:

- Audit `auth.refresh_token.reuse_detected` (actor `User`, no detail). No log line.
- Nothing is recorded for an unknown or expired token, or for a successful refresh.

Edge cases and half-states:

- **Concurrent refresh kills the session.** If a client sends the same refresh token twice at once (two browser tabs, a retry after a timeout whose first request actually succeeded, two web instances sharing a session), one wins and the other is treated as a replay. The loser revokes the family, **including the new token the winner just received** (`TEN03_Concurrency_SimultaneousRefreshesWithTheSameTokenSucceedOnlyOnce`). The person is signed out.
- **A lost response has the same effect.** If the response carrying the new token never reaches the client, the client's next try uses the spent token and ends the session.
- `auth.refresh_token.reuse_detected` is recorded for any revoked token, so it also appears when a client keeps using a token after logout or after a password reset. It does not always mean theft.
- The web console serialises its own refreshes per session (comment in `src/AuthService.Web/Security/SessionService.cs`), so that one request renews and others reuse the result. See part 6 for what happens with several web instances.
- A disabled person's 403 does not revoke their tokens. If a membership is re-enabled, the same token works again.
- Existing access tokens keep working until they expire (up to 15 minutes plus 30 seconds clock allowance) after refresh tokens are revoked.

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| Users are signed out repeatedly | `SELECT occurred_at, actor_id, source_ip FROM audit_entries WHERE action = 'auth.refresh_token.reuse_detected' ORDER BY occurred_at DESC LIMIT 50;` Bursts for one user suggest concurrent refresh from one client. |
| Refresh fails for everyone after a deploy or restore | The API now points at a different database (hashes not found, 401 for all). `SELECT count(*) FROM refresh_tokens WHERE revoked_at IS NULL AND expires_at > now();` should be non-zero. Also check the web console's own session store (part 6). |
| Refresh works but the next API call returns 401 `unauthorized` | Access token invalid: `Auth__Tokens__Issuer` changed, or signing keys changed (section 10). Not a refresh fault. |
| 500 on refresh after a deploy | Migrations not applied (column or table missing). Check logs for the PostgreSQL error. |
| A person's sessions on one family | `SELECT family_id, count(*) AS tokens, max(created_at) AS last_rotation, bool_or(revoked_at IS NULL AND expires_at > now()) AS live FROM refresh_tokens WHERE user_id = '<user id>' GROUP BY family_id ORDER BY 3 DESC;` |
| Table growth | `SELECT count(*) AS total, count(*) FILTER (WHERE expires_at < now()) AS expired FROM refresh_tokens;` |

### Decisions and open questions

- Rotation with family revocation is the standard defence against stolen refresh tokens; the single conditional `UPDATE` makes it safe under concurrency.
- The same 401 for every bad token avoids telling an attacker whether a token ever existed.
- **Risk:** a legitimate duplicate request ends the session (see edge cases). Many OAuth servers allow a short grace period for the previous token; this one has none.
- **Risk:** unbounded growth of `refresh_tokens`. Each rotation adds a row; a console user refreshing every ~15 minutes adds about 100 rows a day. No cleanup exists.
- **Risk:** no absolute session lifetime. A stolen but unused-by-attacker token that is kept rotating by the owner never dies.
- **Open question:** should refresh check `locked_until` or `email_verified_at`? Today a locked account with a live session continues to work.
- **Assumption (confirm):** the 30-day sliding lifetime is the intended product behaviour (comment: "decision Q-09").

---

## Logout

### What it does

A person ends their sign-in on one device. Their refresh token stops working, so the session cannot be renewed. Other devices stay signed in.

Rules:

- The request carries the refresh token. The service revokes every still-usable token in that token's family.
- It always answers 204, whether the token is known, expired, already revoked or unknown. It does not reveal which tokens exist.
- Safe to repeat.
- Access tokens already issued are not revoked. They stop working when they expire (at most 15 minutes plus the 30-second clock allowance).
- Logging out is not audited and not logged.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/logout` | None (the refresh token is the credential) | `LogoutEndpoints.LogoutAsync` (`src/AuthService.Api/Features/Logout/LogoutEndpoints.cs`) | `LogoutService.LogoutAsync` | Body `{ "refreshToken": "..." }`. 204 No Content. |

Flow:

1. Endpoint returns 400 for a blank token.
2. `LogoutService.LogoutAsync` hashes the token and looks up its `family_id`. If unknown, it returns.
3. `db.RevokeRefreshTokenFamilyAsync(familyId, now)` sets `revoked_at` on every token of the family where it was null.

Data: `refresh_tokens.revoked_at`, `refresh_tokens.family_id`.

Background workers: none.

The web console calls this endpoint when a person signs out (`SessionService`, part 6) and removes its own session even if the API is down.

### Configuration and environment

None of its own. The behaviour depends on the token tables only. (`Auth:Tokens:AccessTokenMinutes` sets how long an access token outlives logout.)

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:AccessTokenMinutes` (`Auth__Tokens__AccessTokenMinutes`) | 15 | `IdentityTokenOptions` | How long after logout an already issued access token still works. |

### Failure modes and production debugging

| Situation | Status | `code` |
|---|---|---|
| Missing token | 400 | none (`errors` key `RefreshToken`) |
| Anything else | 204 | none |

- No audit action and no log line. To see that a family was ended, look at `revoked_at` in `refresh_tokens`.
- If the API is down when the web console signs a person out, the console session is deleted but the refresh token stays live until it expires (30 days) or is used. **Assumption (confirm):** the console does not retry the logout call; the comment in `SessionService` says the session is gone "even if the API is down".
- Logging out a token that was already spent by rotation still revokes the family, so it also ends the live descendant. That is intended (comment in `LogoutService`).

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| "I logged out but my other tab is still signed in" | The other tab holds a valid access token for up to 15 minutes. After that its refresh fails with 401. |
| "I logged out on my phone and was signed out on my laptop" | Both are the same family only if one login was shared. Check with the family query in section 5. |

### Decisions and open questions

- Logout revokes the family, not only the presented token, so a client presenting an older token of the session still ends it.
- **Unfinished/inconsistent:** a successful logout leaves no audit entry, unlike login. If security wants "who signed out when", it is not available.
- No "log out everywhere" endpoint exists; only a password reset ends all sessions (`RevokeAllRefreshTokensAsync`).

---

## Current user and current tenant

### What it does

Two read-only calls tell a client who it is dealing with. The first returns the signed-in person's own profile and the organisations
they belong to. The second returns the organisation a tenant-admin token is for. Neither takes an id from the request: who and which
tenant come only from the verified token.

Rules:

- `GET /api/auth/users/me` needs an **identity access token** (no audience). The response has exactly: `id`, `email`, `displayName`, `emailVerified`, `createdAt`, `memberships` (`tenantId`, `tenantName`, `status`). No hash, token or secret field exists (`AUTH_TC30`). It is sent with `Cache-Control: no-store`.
- A query string such as `?id=` or `?email=` is ignored (`AUTH_TC31`).
- A valid token for a user that no longer exists gets 401 `unauthorized`.
- `GET /api/auth/tenant/me` needs a **tenant-admin token**: a product token from token exchange for the built-in Auth Service product (audience `auth-service-tenant`, with `tid` and `sub`). Any active member can obtain one; no permission and no subscription is needed. It returns `id`, `name`, `status` (`Trial`, `Active` or `Suspended` as text) and `createdAt` for the token's tenant only.
- An identity token, another product's token or a service token is refused with 403 on `tenant/me`; an identity-token-only endpoint refuses a token that has an audience with 403.

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `GET /api/auth/users/me` | Policy `IdentityToken` (`IdentityAuthentication.PolicyName`) | `CurrentUserEndpoints.GetCurrentUserAsync` (`src/AuthService.Api/Features/CurrentUser/CurrentUserEndpoints.cs`) | `CurrentUserService.GetAsync` | Returns the caller's profile and memberships. |
| `GET /api/auth/tenant/me` | Tenant context (`RequireTenantContext`, `TenantAuthorization.TenantContext`) | `TenantProfileEndpoints.GetAsync` (`src/AuthService.Api/Features/Tenants/TenantProfileEndpoints.cs`) | direct `AuthDbContext` query | Returns the profile of the tenant in the token's `tid`. |

Flows:

- `users/me`: `ClaimsPrincipalExtensions.GetUserId` reads `sub` and parses a GUID. `CurrentUserService.GetAsync` selects the user (`AsNoTracking`) and `db.ListMembershipsAsync`. A null profile (no `sub`, or no such user) returns 401 `unauthorized`.
- `tenant/me`: `caller.GetTenantId()` reads `tid`. The handler selects the tenant and returns it, or 404 `tenant_not_found` ("No such tenant.").

Data: reads `users`, `tenant_memberships`, `tenants`. No writes.

The tenant-admin token is produced by token exchange (a separate part). The Auth Service's own product is stored at startup by `BuiltInApplicationSync` (`Program.cs`).

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:Issuer` (`Auth__Tokens__Issuer`) | `AuthService` | `IdentityTokenOptions` | Tokens with another issuer are rejected with 401. |
| `Auth:Tokens:ProductTokenMinutes` (`Auth__Tokens__ProductTokenMinutes`) | 15 | `IdentityTokenOptions` | Life of the tenant-admin token used by `tenant/me`. |

### Failure modes and production debugging

| Situation | Status | `code` |
|---|---|---|
| No token, malformed, expired, bad signature, wrong issuer, unknown `kid` | 401 | `unauthorized` ("A valid access token is required."), header `WWW-Authenticate: Bearer` |
| `users/me` with a token that has an audience (product or service token) | 403 | none set by this code (tests assert only the status) |
| `users/me` with a token without `sub` | 403 | none set |
| `users/me`, token valid, user row gone | 401 | `unauthorized` |
| `tenant/me` with an identity token, another product's token or a service token | 403 | none set |
| `tenant/me`, tenant row gone | 404 | `tenant_not_found` |

- Nothing is logged or audited for these reads.
- Edge case: `status` on `tenant/me` can be `Suspended`; the call still succeeds. Suspension is enforced at token exchange, not here.
- Edge case: a console that sends the identity token to `tenant/me` gets 403 and may look like a permissions problem; the cause is the token type.

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| Fresh login, then `users/me` returns 401 | Token validation failure (section 10): issuer mismatch between settings, signing key not published to this instance, or server clocks. Decode the token (without verifying) and check `iss`, `kid`, `exp`. |
| `users/me` returns 403 | The caller sent a product or service token instead of the identity token. Decode and look for an `aud` claim. |
| `tenant/me` returns 403 | The caller sent the identity token. A tenant token must be obtained by token exchange with the built-in application id. |
| `tenant/me` returns 401 only on some instances | Instance has not yet loaded the newest signing key (reload every 60 seconds). |

### Decisions and open questions

- The caller's identity is taken only from the token, never from the request, which removes a class of "read someone else's data" bugs.
- `tenant/me` needs no permission because seeing your own tenant's name needs nothing beyond belonging to it (code comment).
- `users/me` returns `emailVerified` as a boolean but the profile has no way to edit anything; no profile update or password change endpoint exists in these features.
- **Assumption (confirm):** the 403 responses for wrong token types have an empty body. No `OnForbidden` handler is configured, but the tests only assert the status.

---

## Password reset

### What it does

Someone who forgot their password asks for a reset link by email, opens it, and chooses a new password. A successful reset signs them
out everywhere. The service never reveals whether an email is registered.

Rules:

- `forgot` always answers 202 with the same message, whether or not the address exists, and whatever the cooldown does.
- The link is valid **60 minutes** (`TokenMinutes`) and single use.
- An account is not sent a new link if one was created for it less than **60 seconds** ago (`RequestCooldownSeconds`). The refusal is silent.
- Both `forgot` and `reset` count against the **same** per-IP counter: **10 requests per 10 minutes** (`IpLimit`, `IpWindowMinutes`, policy `password-reset`). Guessing links is therefore limited too (`TEN10_TheLimitCoversTryingToResetWithGuessedLinksToo`).
- The new password follows the password policy (8 to 128 characters). It is checked **before** the link is touched, so a weak password does not use up the link (`TEN10_AWeakNewPasswordIsRejectedWith400AndTheLinkIsNotUsedUp`).
- A successful reset: sets the new hash, sets `failed_login_count` to 0 and `locked_until` to null, revokes every refresh token of the person, marks every other unused reset link used, and records `auth.password_reset.completed`.
- A reset does **not** mark the email verified and does not invalidate outstanding email-verification links.
- No email is sent to tell the owner that their password changed (the only templates are `Verification`, `PasswordReset` and `Invitation`).

### How it is implemented

| Verb and route | Auth required | Handler | Service/method | What it does |
|---|---|---|---|---|
| `POST /api/auth/password/forgot` | None; shared rate limit policy `password-reset` | `PasswordResetEndpoints.ForgotAsync` (`src/AuthService.Api/Features/PasswordReset/PasswordResetEndpoints.cs`) | `PasswordResetService.RequestAsync` | Body `{ "email" }`. Always 202 with "If that email belongs to an account, a password reset link has been sent." |
| `POST /api/auth/password/reset` | None; same policy and counter | `PasswordResetEndpoints.ResetAsync` | `PasswordResetService.ResetAsync` | Body `{ "token", "newPassword" }`. 200 `{ "reset": true }`. |

Request flow (`RequestAsync`): find the user by normalised email (none: return); return if a `password_reset_tokens` row for the user has `created_at` newer than the cooldown; create a token with `SecretToken.NewValue()`; add a `PasswordResetToken` (hash only, `expires_at = now + TokenMinutes`) and enqueue `EmailTemplate.PasswordReset` with the raw token in the outbox; one `SaveChangesAsync`. It applies to every account, including unverified ones and people with no password yet.

Reset flow (`ResetAsync`):

1. Endpoint validates `Token` present and `PasswordPolicy.Validate(newPassword)`. Failure gives 400 and nothing is consumed.
2. Hash the token, look it up. Unknown, `used_at` set or `expires_at <= now` gives `InvalidOrExpired`.
3. Load the user and compute the new hash **before** the transaction (the hash is slow).
4. Begin a transaction. Claim the link with one conditional `UPDATE ... WHERE used_at IS NULL AND expires_at > now`; zero rows gives `InvalidOrExpired` (simultaneous use of the same link).
5. Set `password_hash`, `failed_login_count = 0`, `locked_until = null`. Record `auth.password_reset.completed`. `SaveChangesAsync`.
6. `RevokeAllRefreshTokensAsync(user.Id, now)`; mark other unused reset tokens used; commit.
7. Log information: "Password reset completed for account {UserId}; all its sessions were revoked."

Data: `password_reset_tokens` (entity `PasswordResetToken`): `id`, `user_id`, `token_hash` (unique), `created_at`, `expires_at`, `used_at`; index on (`user_id`, `created_at`). Also `users`, `refresh_tokens`, `email_outbox`, `audit_entries`. Rows in `password_reset_tokens` are never purged.

The email has the subject "Reset your password" and a link `{Email:PublicBaseUrl}/reset-password?token=<token>`, handled by the console page `/reset-password` (part 6).

Background workers: `EmailDispatcher` (without it no reset email leaves the system), `RateLimitCleanupWorker`.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:PasswordReset:TokenMinutes` (`Auth__PasswordReset__TokenMinutes`) | 60 | `src/AuthService.Api/Features/PasswordReset/PasswordResetOptions.cs` | Link lifetime. The email states this value (`EmailLifetime.Describe`). |
| `Auth:PasswordReset:RequestCooldownSeconds` (`Auth__PasswordReset__RequestCooldownSeconds`) | 60 | `PasswordResetOptions` | Minimum gap between links per account. |
| `Auth:PasswordReset:IpLimit` (`Auth__PasswordReset__IpLimit`) | 10 | `PasswordResetOptions` | Forgot plus reset requests per IP per window. |
| `Auth:PasswordReset:IpWindowMinutes` (`Auth__PasswordReset__IpWindowMinutes`) | 10 | `PasswordResetOptions` | Window length. |
| `Email:PublicBaseUrl`, `Email:Provider`, `Email:BrevoApiKey`, `Email:FromAddress` | see part 5 | `EmailOptions` | Whether and where the link arrives. |
| `Auth:Proxy:*`, `Auth:RateLimit:Store` | see section 4 | `ProxyOptions`, `RateLimitOptions` | Whether the per-IP limit sees real client addresses and is shared. |

### Failure modes and production debugging

What the caller sees:

| Situation | Status | `code` |
|---|---|---|
| Missing email (forgot) | 400 | none (`errors` key `Email`) |
| Missing token or invalid new password (reset) | 400 | none (`errors` keys `Token`, `NewPassword`: "Password must be between 8 and 128 characters.") |
| Unknown, expired or used link | 410 | `reset_link_invalid` ("This password reset link is invalid or has expired. Request a new one.") |
| Over the per-IP limit (either endpoint) | 429 | `rate_limited`, `Retry-After` |
| Rate-limit store unreachable | 503 | `rate_limit_unavailable` |
| Forgot, any email | 202 | none |

Logged and audited:

- Audit `auth.password_reset.completed` (actor `User`), in the same transaction as the password change.
- Information log "Password reset completed for account {UserId}; all its sessions were revoked." (user id only).
- Nothing is audited or logged for `forgot`, so there is no record of who requested a reset or that a link was sent apart from `password_reset_tokens` and `email_outbox`.

Edge cases and half-states:

- Silent cooldown: a second `forgot` inside 60 seconds returns 202 and sends nothing. The first link remains valid. If the first email failed to send, the person has no working link until the cooldown ends and they ask again.
- A newer link does not invalidate an older one until a reset succeeds; then all are retired.
- **Reset verifies the email (AUTH-266).** `PasswordResetService.ResetAsync` sets `email_verified_at` when it is null (an existing time is kept), so a person who signed up, never verified, and uses password reset can log in at once. This also covers a person who was invited and used reset instead of accepting the invitation. Tests `TEN10_ResettingThePasswordOfAnUnverifiedAccountVerifiesItsEmailSoLoginWorksAtOnce` and `TEN10_ResettingThePasswordOfAVerifiedAccountKeepsTheTimeItWasVerified`.
- Sharing a counter between `forgot` and `reset` means a real person uses two hits per recovery; five people recovering at once behind one NAT address (without proxy configuration, a whole company) exhaust 10 hits in 10 minutes.

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| Reset email not arriving | `SELECT status, attempts, last_error, created_at, sent_at FROM email_outbox WHERE to_address = '<email>' AND template = 'PasswordReset' ORDER BY created_at DESC;` No row: no account, or cooldown suppressed it (`SELECT created_at, expires_at, used_at FROM password_reset_tokens WHERE user_id = '<user id>' ORDER BY created_at DESC;`). `Failed` or old `Pending`: provider or worker problem (part 5). |
| "Link invalid" straight after requesting | A second request produced no second link (cooldown) but the person opened a different, older or already-used link; or 60 minutes passed; or the link was used once (even by a mail scanner that submits it). Check `used_at` and `expires_at` on the user's tokens. |
| Reset worked but person cannot log in | Unverified email (`email_verified_at` null) or the account was locked again. |
| Many people get 429 on forgot or reset | Proxy not configured (`Auth__Proxy__*`), or a shared NAT address. |

### Decisions and open questions

- Single-use link, stored hashed, short life, per-IP limit on both endpoints, and a per-account cooldown: the standard set for this flow. The password check before consuming the link avoids punishing typos.
- Signing the person out everywhere on reset is a deliberate security choice (comment: "Whoever knew the old password, or held a session, is signed out everywhere").
- **Inconsistent:** `forgot` and `reset` share one limit, which is protective but surprising.
- **Open question:** should a successful reset also verify the email, since receiving the link proves control of the mailbox? Today it does not (see edge cases).
- **Open question:** should the owner be told by email that the password was changed? No such template exists.
- **Inconsistent:** the email states "1 hour" regardless of `TokenMinutes`.
- **Inconsistent:** the README text still says the audit log "will record it too once it exists"; it already does.

---

## Password policy, email normalisation, secrets and one-time tokens

### What it does

These shared rules decide what a valid password and email are, how passwords are stored, and how the service creates and stores every
one-time secret it hands to a person (refresh, verification, reset and invitation tokens). They exist so the same rule applies
everywhere and so that a stolen database cannot be replayed as a login or a link.

Rules:

- **Password:** 8 to 128 characters, nothing else (no composition rules, in line with NIST SP 800-63B, per the comment in `PasswordPolicy`). Never trimmed: spaces are valid. Message: "Password must be between 8 and 128 characters." (or "Password is required.").
- Where it applies: signup, password reset, and accepting an invitation as a new person (`AcceptInvitationService`). It is not applied to login.
- **Email:** trimmed and lowercased with `EmailNormalizer.Normalize` (`Trim().ToLowerInvariant()`), "the only place that rule lives". Stored in a `citext` column of at most 254 characters with a unique index.
- **Passwords are stored** as a salted one-way hash (ASP.NET Core Identity `PasswordHasher<User>`, registered as `IPasswordHasher<User>`, scoped, in `Program.cs`), in `users.password_hash` (up to 512 characters). The hash is never returned by an API.
- **One-time tokens** are created by `SecretToken.NewValue()`: 32 random bytes from `RandomNumberGenerator`, written as base64url (43 characters, safe in a link). They are stored as `SecretToken.Hash`: SHA-256 as 64 uppercase hex characters. The raw value exists only in the response or email that delivers it.

| Token | Table.column | Life | Setting | Single use by |
|---|---|---|---|---|
| Refresh token | `refresh_tokens.token_hash` | 30 days, renewed on rotation | `Auth:Tokens:RefreshTokenDays` | Rotation (`revoked_at`) |
| Email verification | `email_verification_tokens.token_hash` | 24 hours | `Auth:EmailVerification:TokenHours` | `used_at` |
| Password reset | `password_reset_tokens.token_hash` | 60 minutes | `Auth:PasswordReset:TokenMinutes` | `used_at` |
| Invitation | `invitations` table (documented with invitations) | see invitations part | `Auth:Invitations:TokenDays` | see invitations part |
| Product client secret | application secrets (documented with products) | n/a | `Auth:Applications:SecretGraceHours` for rotation overlap | n/a |

### How it is implemented

These are shared helpers, not endpoints.

| Piece | File | Used by |
|---|---|---|
| `PasswordPolicy.Validate` | `src/AuthService.Api/Common/PasswordPolicy.cs` | `SignupValidator.TryValidate`, `PasswordResetEndpoints.ResetAsync`, `AcceptInvitationService` |
| `EmailNormalizer.Normalize` | `src/AuthService.Api/Common/EmailNormalizer.cs` | Signup, login, forgot, resend, invite endpoints/services |
| `SecretToken.NewValue`, `SecretToken.Hash` | `src/AuthService.Api/Common/SecretToken.cs` | `IdentityTokenService` (refresh), `EmailVerificationService`, `PasswordResetService`, invitation services, application secrets |
| `IdentityTokenService.Hash` | `src/AuthService.Api/Tokens/IdentityTokenService.cs` | A thin wrapper over `SecretToken.Hash` used by refresh and logout |
| `IPasswordHasher<User>` | `Program.cs` registration | `SignupService`, `LoginService`, `PasswordResetService`, `AcceptInvitationService` |

Notes on the flow of a secret: the raw token is generated, its hash is saved in the token table, and the raw value goes into
`email_outbox.payload_json` (verification, reset) or the HTTP response (refresh). On lookup the presented value is hashed and
matched by an indexed equality on `token_hash`. For emails, the raw token is cleared from the outbox row once the email is sent or given up on.

Libraries: Microsoft.AspNetCore.Identity (`PasswordHasher<User>`), `System.Security.Cryptography` (`RandomNumberGenerator`, `SHA256`), `Microsoft.IdentityModel.Tokens` (`Base64UrlEncoder`).

Background workers: none. (The email outbox worker clears the token from the outbox.)

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| Password length 8 to 128 | constants | `PasswordPolicy.MinLength`, `MaxLength` | Code change only; no setting. |
| Email length 254 | constant | `SignupValidator.MaxEmailLength`, `UserConfiguration` | Code and schema change. |
| Password hasher parameters | library defaults | `Program.cs` (no hasher options are configured anywhere in the repository) | Iteration count and algorithm follow the ASP.NET Core Identity version in use. **Unknown:** the exact values; read the `Microsoft.Extensions.Identity.Core` version in the project files to settle it. |
| Token lifetimes | see table above | options classes | Per feature. |

### Failure modes and production debugging

- A password outside 8 to 128 characters gives 400 with `errors.Password` (signup) or `errors.NewPassword` (reset). A password with leading or trailing spaces is accepted and stored with them, so people who paste a password with a trailing space may then fail to log in without the space.
- An email that differs only by case or surrounding spaces is treated as the same account everywhere. An email with different Unicode forms, a plus tag or dots is a different account (no further normalisation exists).
- Audit entries refuse any detail that is not a short code (`AuditLog.Record` checks `^[a-z0-9_.-]{1,128}$`), so a password or token cannot be recorded by mistake. A violation throws an `ArgumentException`.
- Logs mention user ids, never emails, tokens or links (stated in the README and visible in `LoginService` and `PasswordResetService`).
- Raw tokens appear in two places: the email link, and `email_outbox.payload_json` until the email is sent or fails permanently. A database read before delivery can see them (the README says this explicitly).

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| "My password is correct but login fails" because of a stored trailing space or a keyboard layout difference at signup | No direct check possible (only a hash is stored). Ask the person to reset; the reset path uses the same rules. |
| Two accounts that look like one address | `SELECT email FROM users WHERE email ILIKE '%<local part>%';` Plus tags and Unicode variants are distinct accounts by design. |
| Hash cost making login slow under load | Login does one hash per request including for unknown accounts; check CPU and the login route duration in `auth_http_request_duration_seconds_count` and its sibling series. Per-IP limits bound it. |

### Decisions and open questions

- Length only, no composition rules: the stated policy.
- Unsalted SHA-256 is acceptable for the one-time tokens because they are 256 bits of randomness (offline guessing is not feasible); passwords use the salted, slow hasher instead.
- **Risk:** `LoginService` treats only a `Failed` verification result as a failure and never saves a re-hashed password, so if the hashing defaults are raised in a framework upgrade, existing hashes stay at the old strength.
- **Risk:** the token is compared by database equality on the hash, not a constant-time comparison. With 256-bit random tokens this is not exploitable in practice.
- **Open question:** no maximum length is applied at login; only the server's request size limit bounds the password length hashed. **Assumption (confirm):** acceptable because the per-IP limit applies.
- **Open question:** no breached-password or common-password check exists.

---

## The identity access token

### What it does

After login or refresh the client holds a short-lived signed token, the identity access token. It proves who the person is to the Auth
Service's own endpoints (for example `GET /api/auth/users/me`, token exchange). It does not say which organisation the person is working in or
what they may do; a product or tenant token from token exchange does that.

Rules:

- **Format:** a JWT signed with ECDSA P-256 / SHA-256 (`SecurityAlgorithms.EcdsaSha256`, "ES256"). The header names the signing key by `kid`.
- **Claims:** exactly `sub` (the user id), `jti` (a new GUID), `iss`, `iat`, `nbf`, `exp` (confirmed by the test `AUTH_TC5_LoginWithCorrectCredentialsReturnsTokensAndMemberships`). No `aud`, no `tid`, no permissions, no email.
- **Lifetime:** 15 minutes (`Auth:Tokens:AccessTokenMinutes`); `exp` equals issue time plus that.
- **Validation** (`IdentityAuthentication.AddIdentityTokenAuthentication`): signature must verify against a published key chosen by `kid`; only ES256 is accepted; issuer must equal `Auth:Tokens:Issuer`; lifetime is checked with **30 seconds** of clock allowance (`ClockSkew`); audience is not validated; signed tokens are required.
- **Policies:** `IdentityToken` requires an authenticated user, a `sub` claim and **no** `aud` claim. `PlatformAdmin` is the same plus a platform-admin requirement. `ServiceToken` is for product machine tokens.
- **No revocation.** An issued access token is valid until it expires. Logout, password reset, locking and disabling do not invalidate it.

### How it is implemented

Pieces:

| Piece | File | Role |
|---|---|---|
| `IdentityTokenService.CreateAccessToken` | `src/AuthService.Api/Tokens/IdentityTokenService.cs` | Builds and signs the token with `SigningKeyService.CurrentSigningKey`. |
| `IdentityTokenOptions` | `src/AuthService.Api/Tokens/IdentityTokenOptions.cs` | Issuer, lifetimes, key settings, encryption key. Section `Auth:Tokens`. |
| `AddIdentityTokenAuthentication` | `src/AuthService.Api/Tokens/IdentityAuthentication.cs` | JWT bearer handler and the three authorization policies. |
| `SigningKeyService` | `src/AuthService.Api/Tokens/SigningKeyService.cs` | Holds keys in memory; `GetValidationKeys(kid)` returns the published key with that id; reloaded from the database every `SigningKeyRefreshSeconds`. |
| `KeyEncryption` | `src/AuthService.Api/Tokens/KeyEncryption.cs` | AES-256-GCM encryption of stored private keys with `Auth:Tokens:KeyEncryptionKey`. |
| `SigningKeyWorker` | `src/AuthService.Api/Tokens/SigningKeyWorker.cs` | Reloads keys and rotates on schedule. |

Issue flow: `CreateAccessToken(userId, now)` sets issuer, claims, times and `SigningCredentials(CurrentSigningKey, EcdsaSha256)` and calls `JsonWebTokenHandler.CreateToken`. Login and refresh both call it. `expiresIn` in the response is `(ExpiresAt - now)` in whole seconds.

Validation flow on a protected endpoint: `UseAuthentication` runs the JWT bearer handler with the parameters above (`MapInboundClaims = false`, so the claim is `sub`, not a long Microsoft URI). `IssuerSigningKeyResolver` calls `signingKeys.GetValidationKeys(kid)`; a token with no `kid`, or a `kid` that is not currently published, matches nothing and is refused. If authentication fails, `OnChallenge` returns 401 with code `unauthorized`, "A valid access token is required.", and `WWW-Authenticate: Bearer`. Then `UseAuthorization` evaluates the policy; failure returns 403.

Other tokens signed by the same keys: product tokens (`CreateProductToken`, with `aud`, `tid`, `permissions`) and service tokens (`CreateServiceToken`, `aud` = `auth-service`). They are described with token exchange and products. The signing key lifecycle and the public key document at `/.well-known/jwks.json` are described in the tokens and keys part.

Data: `signing_keys` table (`SigningKey`) holds the public key and the encrypted private key. The identity token itself is not stored anywhere.

Background workers: `SigningKeyWorker` (interval `Auth:Tokens:SigningKeyRefreshSeconds`, default 60). If it stops, an instance keeps using the keys it loaded and will not pick up a rotated key; a token signed with a key the instance does not know is refused.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:Issuer` (`Auth__Tokens__Issuer`) | `AuthService` | `IdentityTokenOptions` | `iss` of every token. Changing it invalidates all outstanding tokens and must match every product's configuration. In `deploy/docker-compose.yml` it comes from `AUTH_ISSUER` (default `AuthService`). |
| `Auth:Tokens:AccessTokenMinutes` (`Auth__Tokens__AccessTokenMinutes`) | 15 | `IdentityTokenOptions` | Lifetime; also `expiresIn`. |
| `Auth:Tokens:KeyEncryptionKey` (`Auth__Tokens__KeyEncryptionKey`) | none | `IdentityTokenOptions`, `KeyEncryption` | **Required** outside Development and Testing (32 random bytes, base64). Without it, or if not valid base64 of 32 bytes, startup fails. Losing it makes stored signing keys unreadable. Secret; pass through a secret store (`deploy/docker-compose.yml` reads `AUTH_KEY_ENCRYPTION_KEY`). In Development and Testing a fixed public key is used. |
| `Auth:Tokens:SigningKeyRotationDays` (`Auth__Tokens__SigningKeyRotationDays`) | 90 | `IdentityTokenOptions` | How long a key signs. |
| `Auth:Tokens:SigningKeyPrePublishHours` (`Auth__Tokens__SigningKeyPrePublishHours`) | 24 | `IdentityTokenOptions` | New key is published this long before it signs. |
| `Auth:Tokens:SigningKeyPostRetireHours` (`Auth__Tokens__SigningKeyPostRetireHours`) | 24 | `IdentityTokenOptions` | A retired key still validates for this long. |
| `Auth:Tokens:SigningKeyRefreshSeconds` (`Auth__Tokens__SigningKeyRefreshSeconds`) | 60 | `IdentityTokenOptions` | How often each instance reloads keys. |
| `Auth:Tokens:ServiceTokenMinutes`, `ProductTokenMinutes` | 10, 15 | `IdentityTokenOptions` | Other token kinds; not used by identity tokens. |

The API refuses to start if there is no signing key it can load: `Program.cs` calls `SigningKeyService.RefreshAsync` before serving, so a missing encryption secret or unreachable database stops the deployment rather than the first login.

### Failure modes and production debugging

| Situation | Status | `code` |
|---|---|---|
| Missing, malformed, expired, wrongly signed, wrong issuer, unknown `kid`, wrong algorithm | 401 | `unauthorized` |
| Valid token that carries an `aud` claim, on an identity endpoint | 403 | none set |
| Valid token with no `sub` claim | 403 | none set |
| User row gone (only checked by `users/me`) | 401 | `unauthorized` |

- Failures are not logged or audited by this code. `appsettings.json` sets the `Microsoft.AspNetCore` log level to Warning. **Assumption (confirm):** the JWT bearer handler's own "token rejected" messages are below that level, so rejected tokens are probably not logged.
- No metric counts rejected tokens as such. `auth_http_requests_total` (labels method, route template, status) shows 401 and 403 counts per route.
- **Emergency signing key rotation** invalidates all identity tokens at once (`TOK02_Rotation_EmergencyRotationReplacesTheKeyAtOnceAndInvalidatesItsTokens`); refresh tokens are opaque and still work, so clients recover by refreshing.
- With several instances, an instance that has not reloaded yet can refuse a token signed by a key it does not know until its next reload (up to `SigningKeyRefreshSeconds`). The pre-publish period (24 hours) means this should only happen for emergency rotations.
- Clock differences larger than 30 seconds between the instance that issues a token and the one that validates it cause `nbf`/`exp` rejections.

Likely production causes and how to confirm:

| Cause | How to confirm |
|---|---|
| 401 on every call after a change | `Auth__Tokens__Issuer` changed on the API or does not match; compare the `iss` in a decoded token with the setting. |
| 401 after an emergency rotation | Expected for old tokens; clients should refresh. Check audit action `auth.signing_key.rotated` in `audit_entries`. |
| 401 right after login on some instances | Instance clock wrong, or instance cannot read the database to load keys. Check the process clock and startup logs. |
| API refuses to start | Missing or invalid `Auth__Tokens__KeyEncryptionKey` ("is not configured" / "is not valid base64" / "must be 32 bytes"), or stored keys were encrypted with a different secret. |
| No tokens can be issued | `SigningKeyService.HasCurrentKey` is false; the readiness check (`/health/ready`, platform part) reports it. |
| A product says tokens are invalid | The product's issuer or JWKS URL differs. This part's identity tokens are not used by products; see token exchange. |

### Decisions and open questions

- Identity tokens carry no tenant and no audience so that a product token (scoped to one tenant and one product) can never be confused with an identity token; the `IdentityToken` policy rejects any token with `aud`.
- ES256 only (`ValidAlgorithms`), so a token cannot pick a weaker algorithm.
- A 15-minute life with no revocation is the trade-off: disabling a membership or locking an account takes effect on refresh, not immediately on existing access tokens.
- **Risk:** no access-token revocation list, so a stolen access token is usable for up to 15 minutes plus 30 seconds.
- **Risk:** rejected tokens are not logged or counted, so a misconfigured client is hard to see from the server side except through 401 rates.
- **Assumption (confirm):** the 403 for a wrong token type is not intended to carry a `code`. All other errors do.

---

## Symptom to where to look (this part)

| Symptom a user or on-call engineer reports | Likely cause | First place to look (file and method, setting, table) | What to check |
|---|---|---|---|
| User says the verification link says it is invalid or expired (410 `verification_link_invalid`) | Link older than 24 hours, already retired, mangled by the mail client, or generated by another environment | `EmailVerificationService.VerifyAsync`; table `email_verification_tokens` | `expires_at` and `used_at` for the user's rows; token length is 43; which `Email__PublicBaseUrl` the email used. Tell the user to use resend. |
| User never received the verification or reset email | Silent 60-second cooldown, outbox `Pending` or `Failed`, dispatcher disabled, provider not configured, sender not verified | `EmailVerificationService.ResendAsync`, `PasswordResetService.RequestAsync`; table `email_outbox`; part 5 | `SELECT status, attempts, last_error FROM email_outbox WHERE to_address = '<email>' ORDER BY created_at DESC;`; `Email__DispatcherEnabled`; token row `created_at`. |
| A user is locked out and says "invalid credentials" with the right password | Five wrong passwords in a row; `locked_until` in the future | `LoginService.LoginAsync`, `RegisterFailureAsync`; table `users` | `SELECT failed_login_count, locked_until FROM users WHERE email = '<email>';`; audit `auth.account.locked`. A password reset clears it; otherwise wait 15 minutes. |
| "Invalid credentials" for a correct password and the account is not locked | Person never set a password (invitation pending), different environment, email typo, password typed with or without a space | `LoginService.LoginAsync` (null `password_hash` branch); audit detail `unknown_account` | `password_hash IS NULL`; audit rows; same database as the console uses. |
| 403 `account_disabled` | All memberships `Disabled` | `AccountAccess.IsDisabled`; `tenant_memberships.status` | Membership query in Login section; re-enable a membership via the member management features. |
| Users are repeatedly signed out of the console | Concurrent or lost refresh response triggers reuse detection; password reset; refresh token expired; logout elsewhere | `RefreshService.RefreshAsync`; audit `auth.refresh_token.reuse_detected`; table `refresh_tokens` | Bursts of reuse entries for one user; `revoked_at` pattern per family; whether more than one console instance shares a session. |
| Refresh fails for everyone after a deploy or restore | API now uses another database (hashes unknown) or the migration was not applied | `RefreshService.RefreshAsync`; `ConnectionStrings__AuthDb`; table `refresh_tokens` | Live token count; application log for PostgreSQL errors; migrations (ADR 0003). |
| Refresh works but the next API call returns 401 `unauthorized` | Issuer changed, signing key changed, clock skew over 30 seconds | `IdentityAuthentication`, `SigningKeyService.GetValidationKeys`; `Auth__Tokens__Issuer` | Decode token: `iss`, `kid`, `exp`; audit `auth.signing_key.rotated`. |
| Many users get 429 `rate_limited` on login, forgot or resend at once | Service behind a proxy without `Auth:Proxy`, so one shared address; or a shared NAT | `SharedRateLimitMiddleware`, `RateLimitPolicies.Resolve`; `Auth__Proxy__*`; table `rate_limit_counters` | `source_ip` in `audit_entries` is the same address for everyone; counters with high `hits`. |
| 503 `rate_limit_unavailable` on login or reset | Database used for counters is unreachable or slow | `SharedRateLimitMiddleware`; `PostgresRateLimitStore` | Error log "The rate-limit store could not be reached..."; database health. |
| Password reset link "invalid" right after requesting | Link expired (60 minutes), already used, a later request produced no new link, or an older link was opened | `PasswordResetService.ResetAsync`; table `password_reset_tokens` | `created_at`, `expires_at`, `used_at` for the user. |
| Email says the link lasts 24 hours or 1 hour but behaviour differs | Settings changed but the email text is fixed | `EmailTemplates.Render`; `Auth:EmailVerification:TokenHours`, `Auth:PasswordReset:TokenMinutes` | Compare settings with the text; fix by updating the template. |
| New customer cannot sign up: "email already registered" but has no working account | An earlier unverified signup holds the address | `SignupService.SignUpAsync`; `users` | `email_verified_at IS NULL` for the address; point them to resend or reset. |
| `GET /api/auth/users/me` returns 403 | A product or service token was sent (has an `aud`) | `IdentityAuthentication` (`IdentityToken` policy) | Decode the token and look for `aud`. |
| `GET /api/auth/tenant/me` returns 403 | An identity token was sent instead of a tenant-admin token | `TenantAuthorization.TenantContext` | Token needs `aud` = `auth-service-tenant` and a `tid`; obtain through token exchange. |
| Signup returns 500 | Database error part-way (rolled back), or migration missing | `SignupService.SignUpAsync`; application log | Postgres error text; row counts unchanged. |
| `refresh_tokens` table is very large | Rotated tokens are never purged | `RefreshTokenConfiguration`; no cleanup worker | `SELECT count(*), count(*) FILTER (WHERE expires_at < now()) FROM refresh_tokens;` |
| API will not start after a configuration change | Key encryption secret missing or wrong, bad `Auth:Proxy`, bad `Auth:RateLimit:Store` | `KeyEncryption` constructor; `Program.cs` startup checks | Startup exception text names the setting. |

## Highest-risk areas in this part

1. **Refresh-token rotation under concurrency** (`RefreshService.RefreshAsync`): a duplicate or lost-response refresh revokes the whole session, including the newest token, and the audit action `auth.refresh_token.reuse_detected` is also written for tokens revoked by logout or password reset. Review whether a short grace window is wanted and whether the audit signal should distinguish theft from a duplicate.
2. **Per-IP limits depend on correct proxy settings** (`Auth:Proxy`, `RateLimitPolicies.Resolve`): behind a load balancer without it, login, forgot and resend share one address, turning a per-IP limit into a platform-wide one (an outage for sign-in) and making audit `source_ip` useless. Review deployment configuration and add an alert on 429 volume.
3. **Account lock-out as a denial of service** (`LoginService.RegisterFailureAsync`): anyone who knows an email can keep an account locked, with no unlock path other than waiting or a password reset. Signup, refresh and verify are unthrottled. Review whether to add support tooling and limits on signup.
4. **Unbounded growth and no absolute session limit** (`refresh_tokens`, `email_verification_tokens`, `password_reset_tokens`, `audit_entries`): nothing purges the three token tables, and refresh renewal is sliding with no maximum age. Review cleanup jobs and a maximum session age.
5. **Access tokens cannot be revoked** (`IdentityAuthentication`): lock, disable, logout and password reset do not stop an already issued token for up to 15 minutes plus 30 seconds, and refresh does not check lock or verification. Review if that window is acceptable for admin actions.
6. **Email dependence** (`EmailTemplates`, `email_outbox`): verification and reset work only if the outbox worker and provider work, and the silent cooldown hides failures. Review monitoring of `email_outbox` `Failed` rows.

## Unknowns and assumptions to confirm

- **Assumption (confirm):** there is no email-domain allow-list or disposable-address check on signup (none in the code read).
- **Assumption (confirm):** unverified accounts are meant to remain in `users` indefinitely, holding their email address; no expiry exists.
- **Assumption (confirm):** a person with zero memberships may log in and receive an empty membership list.
- **Assumption (confirm):** the account lock is not meant to revoke refresh tokens or end existing sessions.
- **Assumption (confirm):** the 30-day sliding refresh lifetime with no absolute maximum is the intended behaviour (code comment cites decision Q-09).
- **Assumption (confirm):** the web console does not retry a failed logout call, so a refresh token can stay live after the person believes they signed out (per the `SessionService` comment that the console session is removed even if the API is down).
- **Assumption (confirm):** the 403 responses for a wrong token type (identity versus product or service token, missing `sub`) are intended to have no `code`; tests assert only the status.
- **Assumption (confirm):** no maximum length at login is acceptable because the per-IP limit applies.
- **Unknown:** the exact password-hash algorithm parameters (iteration count, PRF). No hasher options are set in the repository, so they follow the `Microsoft.Extensions.Identity.Core` default for the framework version; the package version in the project files would settle it.
- **Unknown:** the file names of the other reference parts (email delivery, tokens and keys, token exchange, invitations). Links to those parts in this file point at the README until the names are fixed.
- **Open questions for the owner:** whether a successful password reset should mark the email verified; whether the owner should be emailed when their password changes; whether signup needs a rate limit; whether refresh should check lock and verification; whether a grace period for concurrent refresh is wanted; whether `refresh_tokens` and the other token tables need a cleanup job.
