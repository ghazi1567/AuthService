# Organisations, members and roles

> Part 2 of the Auth Service reference. Overview, glossary and symptom index: [README](README.md).

This part covers how an organisation (a *tenant*) is created, who belongs to it, how people are invited, what roles are, and how the
Auth Service protects its own admin endpoints. Subscriptions and seats themselves are in
[part 3, products and subscriptions](03-products-and-subscriptions.md).

Vocabulary used below: a *tenant* is an organisation. A person has one global identity (a `users` row) and one *membership* in each
tenant (a `tenant_memberships` row), as decided in `docs/decisions/0004-global-identity-with-tenant-memberships.md`. Roles and
product access attach to the membership, never to the person. A *tenant-admin token* is a product token for the Auth Service's own
built-in product (decision `docs/decisions/0005-auth-service-as-its-own-product.md`).

## Contents

- [Organisations (tenants)](#organisations-tenants)
- [Memberships and the member list](#memberships-and-the-member-list)
- [Invitations](#invitations)
- [Roles](#roles)
- [Assigning roles to members](#assigning-roles-to-members)
- [The permission catalog and tenant-admin permissions](#the-permission-catalog-and-tenant-admin-permissions)
- [Member product access (seats)](#member-product-access-seats)
- [Symptom to where to look (this part)](#symptom-to-where-to-look-this-part)
- [Highest-risk areas in this part](#highest-risk-areas-in-this-part)
- [Unknowns and assumptions to confirm](#unknowns-and-assumptions-to-confirm)

---

## Organisations (tenants)

### What it does

An organisation is the business that uses the platform. Its first one is created at signup (part 1). A person who is already signed
in can create a further organisation without signing up again, and becomes its Owner. Any member of an organisation can read its
profile (name, status, creation time). An organisation also has a *status*, which the platform can use to switch it off.

Rules:

- **Creating a further organisation (TEN-09).** `POST /api/auth/tenants` with `{"tenantName": "..."}` and an identity token. The
  name is trimmed, required, and at most 200 characters (`SignupValidator.MaxTenantNameLength`, also the `tenants.name` column size).
- **The Owner is the caller named by the token**, never anyone named in the request.
- **Tenant, Owner role and membership are created in one transaction** (`TenantProvisioning.AddTenantWithOwner`), the same code signup
  uses, so the two paths cannot create tenants differently. A failure part-way leaves nothing behind (test
  `AUTH_TC34_FailureAfterTheTenantRowIsCreatedRollsEverythingBack`).
- **A person whose every membership is Disabled cannot create an organisation** (`403` `account_disabled`). Without this, a token
  issued before the person was disabled could be used to give them a fresh Active membership. `AccountAccess.IsDisabled` is true only
  when the person has at least one membership and all are Disabled. An Invited-only person is not "disabled".
- **Names are not unique** and there is no limit on how many organisations one person can create. I found no rate limit on the
  endpoint (only login, password reset, email-verification resend, token exchange and service tokens use `RequireSharedRateLimit`).
- **Reading the profile (TEN-05).** `GET /api/auth/tenant/me` returns `{id, name, status, createdAt}` for the tenant in the token's
  `tid` claim. A tenant id in the path, query string or body is never read (tests `AUTH_TC13_...` and `TEN05_...`). No permission is
  needed beyond holding a tenant-admin token.
- **Status.** `TenantStatus` is `Trial`, `Active`, `Suspended`, stored as text in `tenants.status` (max 20 characters). Every tenant
  created by the code starts `Active`. Nothing in `src` ever sets `Trial`.
- **What `Suspended` blocks** (all read from the database at the time of the call):
  - Token exchange for any product, including the Auth Service's own: an *Active member* gets `403` `tenant_suspended`; anyone else
    still gets the plain `403` `exchange_denied` (`TokenExchangeService.ExchangeAsync`). So the admin console cannot be opened.
  - **Every tenant-admin request** (AUTH-268): `ActiveTenantRequirement` is part of the `TenantContext` policy, and `ActiveTenantHandler` reads
    the status of the token's `tid` on each call. A suspended organisation gets `401` `tenant_suspended` (with `WWW-Authenticate: Bearer`, from
    `TenantSuspendedResultHandler`), so a token issued before the suspension stops working at once and works again after reactivation. A missing
    permission is still an empty `403`. The console answers the 401 by dropping its cached token and reloading, and the exchange then says why.
  - `GET /api/auth/users/me/applications` (`MyApplicationsEndpoints`): `403` `tenant_suspended` for an Active member,
    `tenant_access_denied` for anyone else.
  - Subscribing the tenant to a product (`SubscribeTenantService`) and a product changing its seat limit (`SeatLimitService`): `409` `tenant_suspended`.
  - Accepting an invitation (`AcceptInvitationService`): `403` `tenant_suspended`, checked before the link is claimed or a password hashed,
    so the link keeps working after reactivation.
  - API keys: `ApiKeyValidationService` treats a key of a suspended tenant as invalid (`401` `invalid_api_key`).
- **What `Suspended` does not block.** Login and refresh still work (the person is not suspended, their organisation is). A product can still
  suspend or cancel its own subscription, which only takes access away. Creating another organisation still works.
- **Suspending and reactivating** (AUTH-268, decision 0013). `PUT /api/auth/admin/tenants/{tenantId}/status` with `{"status": "Suspended" | "Active", "reason": "<code>"}`
  (platform admin; `TenantAdminEndpoints.SetStatusAsync`, `TenantStatusService.SetAsync`). `status` is `Active` or `Suspended` (any case; a number or
  another word is `400`). `reason` is required and one of `TenantStatusValidator.Reasons`: `non_payment`, `abuse`, `security`, `customer_request`,
  `resolved`, `other` (`400` otherwise): a code, because the audit log's detail column holds only short codes. The organisation row is locked
  (`FOR UPDATE`); a real change writes `auth.tenant.suspended` or `auth.tenant.reactivated` (actor = the admin, `tenant_id` = the organisation, detail = the
  reason code); setting the status it already has is `200` and writes nothing. An unknown id is `404` `tenant_not_found`.
  `GET /api/auth/admin/tenants` lists organisations by name (`id`, `name`, `status`, `memberCount`, `createdAt`), and the platform console's Organisations
  page (`/platform/organisations`) does both with a confirmation.

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `POST /api/auth/tenants` | Identity token (policy `IdentityToken`) | `CreateTenantEndpoints.CreateTenantAsync` | `CreateTenantService.CreateAsync` | Creates a tenant, its built-in Owner role and the caller's Active membership. `201` with `{tenantId, tenantName, membershipId}`. |
| `GET /api/auth/tenant/me` | Tenant-admin token, any permissions (`RequireTenantContext()`) | `TenantProfileEndpoints.GetAsync` | direct `AuthDbContext` read | Returns the profile of the token's tenant. |

Flow of `POST /api/auth/tenants`:

1. The `IdentityToken` policy accepts only a signed token with a `sub` and **no** `aud` claim. A product token is refused with `403`
   (test `TEN09_AProductTokenWithAnAudienceIsRefusedWith403`); no token is `401`.
2. `CreateTenantAsync` trims `TenantName`; empty gives a `400` validation problem (field `TenantName`, "Tenant name is required."),
   over 200 characters gives "Tenant name must be at most 200 characters."
3. `CreateTenantService.CreateAsync` checks the user still exists (otherwise `401` `unauthorized`), loads the user's membership
   statuses and refuses with `AccountDisabled` if `AccountAccess.IsDisabled`.
4. `db.AddTenantWithOwner` adds four rows in memory: `Tenant` (Active), `TenantMembership` (Active), `Role` (name `Owner`,
   `IsBuiltIn = true`), `MembershipRole` (links them). Ids are `Guid.CreateVersion7()`.
5. `audit.Record(AuditActions.TenantCreated, AuditActor.User(userId), tenantId)` and one `SaveChangesAsync` commit everything.

The new Owner has **no product access** yet: seats come only when the tenant subscribes (see
[part 3](03-products-and-subscriptions.md)). The Auth Service's own product needs no seat.

Data:

| Table (entity) | Columns used | Notes |
|---|---|---|
| `tenants` (`Tenant`) | `id`, `name` (200), `status` (text, 20), `created_at` | No unique index on `name`. |
| `tenant_memberships` (`TenantMembership`) | `id`, `tenant_id`, `user_id`, `status`, `created_at` | Unique index on (`tenant_id`, `user_id`): one membership per person per tenant. Alternate key (`tenant_id`, `id`). |
| `roles` (`Role`) | `id`, `tenant_id`, `name` (citext, 100), `is_built_in`, `created_at` | Unique index `ix_roles_tenant_id_name`. |
| `membership_roles` (`MembershipRole`) | `id`, `tenant_id`, `membership_id`, `role_id`, `assigned_at` | Foreign keys include `tenant_id`, so the database refuses a role and membership from different tenants. |

Libraries and services: ASP.NET Core minimal APIs, EF Core with Npgsql. No external service. No background worker.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:AccessTokenMinutes` / `Auth__Tokens__AccessTokenMinutes` | 15 | `IdentityTokenOptions` | Lifetime of the identity token needed to create an organisation. |
| `Auth:Tokens:ProductTokenMinutes` / `Auth__Tokens__ProductTokenMinutes` | 15 | `IdentityTokenOptions` | How long a suspended tenant's already-issued tenant-admin tokens keep working. |
| `ConnectionStrings:AuthDb` / `ConnectionStrings__AuthDb` | none (required) | `Program.cs` | Without it startup throws "Connection string 'AuthDb' is not configured." |

There is no setting for the tenant name length or for the number of organisations per person. Both are fixed in code.

### Failure modes and production debugging

What the caller sees:

| Case | Status and `code` |
|---|---|
| No token, bad signature, expired, wrong issuer | `401` `unauthorized` ("A valid access token is required.") |
| Product token or service token on `POST /api/auth/tenants` | `403`, empty body (default authorization failure) |
| Blank or over-long `tenantName` | `400` validation problem, field `TenantName` |
| Token valid but the user row is gone | `401` `unauthorized` |
| Every membership Disabled | `403` `account_disabled` |
| `GET /api/auth/tenant/me` with an identity token, a product token for another product, or a service token | `403`, empty body |
| `GET /api/auth/tenant/me` and the tenant row is missing | `404` `tenant_not_found` (cannot normally happen, memberships have a restricted foreign key) |

Logged and audited:

- Audit action `auth.tenant.created` (`AuditActions.TenantCreated`), actor type User, `tenant_id` set, no detail.
- These services take no `ILogger`; a refused request writes no log line. An unexpected exception becomes a plain `500` problem via
  `UseExceptionHandler` and is counted by `RequestMetricsMiddleware`.

Edge cases and half-states:

- A suspension can be forgotten, since nothing reminds anyone. A person reports "nothing opens, `tenant_suspended`" and the
  console shows "<name> is suspended" (`ConsoleDenied.razor`). The audit log says who suspended it and why.
- A suspended organisation's admins who hold a tenant-admin token are refused at once (`401` `tenant_suspended`), not after the token expires.
- The web console tells a suspended organisation's members to "Ask the platform administrator to switch it back on", and the platform
  console now has the control (`/platform/organisations`, Reactivate).

Likely causes and how to confirm:

| Symptom | Cause | Confirm |
|---|---|---|
| `403` `tenant_suspended` on exchange, or `401` `tenant_suspended` on a tenant-admin call | `tenants.status = 'Suspended'` | `SELECT id, name, status FROM tenants WHERE id = '<tenant>';` and the audit log: `auth.tenant.suspended` (detail = the reason code) |
| Person cannot create an organisation, `account_disabled` | All their memberships Disabled | `SELECT tenant_id, status FROM tenant_memberships WHERE user_id = '<user>';` |
| New organisation has no Owner role | Not possible through the code path (one transaction). Suspect manual SQL. | `SELECT id, name, is_built_in FROM roles WHERE tenant_id = '<tenant>';` |
| `500` on create | Database down, or a migration missing | Readiness endpoint, `__EFMigrationsHistory` (migrations are applied explicitly, never on startup) |

To reactivate a suspended organisation use the platform console (Organisations, Reactivate) or `PUT /api/auth/admin/tenants/{id}/status` with `{"status": "Active", "reason": "resolved"}`.
It applies at once, with no restart, and is audited. (Changing `tenants.status` in SQL still works and takes effect the same way, but leaves no audit entry.
Older tests do that with `ExecuteUpdateAsync`.)

### Decisions and open questions

- A single `AddTenantWithOwner` is shared by signup and create-organisation so they cannot drift (comment in `TenantProvisioning`).
- The tenant of a request always comes from the token's `tid`, never from the request (decision 0005).
- `404`, not `403`, for anything in another tenant, so ids are not confirmed.
- **Inconsistent / unfinished:** `TenantStatus.Trial` exists and the console contracts mention it, but nothing sets it.
- **Fixed (AUTH-268, decision 0013):** suspension has an endpoint, an audit entry (with a reason code) and a platform console screen, and a
  tenant-admin token no longer outlives it. Open: products are not told by a webhook when an organisation is suspended or reactivated; the
  reason is one of six codes, not free text.
- **Assumption (confirm):** unlimited organisations per person is intended. The code has no cap and no comment saying either way.

---

## Memberships and the member list

### What it does

A membership says "this person belongs to this organisation". It has one of three states, shown in the member list. Admins can see
who belongs to the organisation, with their roles and the products they have a seat in, and can disable or re-enable a person's
membership.

Member states (`MembershipStatus`, stored as text in `tenant_memberships.status`):

| State | Meaning | How it is reached |
|---|---|---|
| `Invited` | The person has been invited and has not accepted. Holds roles and optional seats. | Invitation (`InviteUserService`). |
| `Active` | A working member. | Created `Active` for an Owner; or by accepting an invitation; or by enabling a Disabled one. |
| `Disabled` | Access removed. Roles, product access rows and history stay. | `PATCH` with `Disabled`. |

Rules:

- **Enable or disable** with `PATCH /api/auth/tenants/me/members/{membershipId}` and `{"status": "Active" | "Disabled"}`. The value is
  read case-insensitively; blank, a number, `Invited` or any other word is `400` ("Status must be Active or Disabled.",
  `MemberStatusValidator.TryRead`).
- **Setting the status a member already has changes nothing** and returns `200` with the member.
- **Only this tenant's membership changes.** The same person's memberships elsewhere are untouched.
- **Only the status changes.** Roles, access rows and history stay, so enabling restores the member as they were.
- **An Invited person cannot be enabled** (`409` `membership_invited`: "They become Active by accepting it."). They can be disabled,
  which also withdraws the invitation but keeps the membership; to take an invitation back cleanly use `DELETE .../invitations/{membershipId}` (see Invitations).
- **Disabling frees seats.** A Disabled member's `application_access` rows stay but are not counted
  (`SeatsUsedAsync` counts Active memberships and Invited ones whose invitation has not expired; see Invitations).
- **Enabling re-checks seats.** For every product the member has access to, the subscription row is locked and, if the subscription
  has a `max_seats` limit and the seats used already equal or exceed it, the answer is `409` `no_seat_available` naming the products,
  and the member stays Disabled. A subscription that is missing or unlimited is skipped.
- **A tenant keeps an Owner.** Changing the status of an Owner, in either direction and from any state (Active, Invited, Disabled), needs
  the caller to be an Active Owner (`403` `owner_role_restricted`). Disabling an *Active* Owner also needs another Active Owner (`409`
  `last_owner`). Disabled and Invited Owners do not count as Owners for this.
- **A disabled member cannot get a token for this tenant**: the next exchange is refused. Tokens already issued run until they expire.
- **Person-level effect:** a person all of whose memberships are Disabled counts as a disabled account (`AccountAccess.IsDisabled`)
  and cannot log in or refresh a session (part 1).

The list (`GET /api/auth/tenants/me/members`) returns, per person, `{membershipId, email, displayName, status, roles[{id,name}],
applications[{applicationId, applicationKey}], createdAt}`, ordered by email then membership id. Invited and Disabled people are
included. `roles` is sorted by name (case-insensitive) and `applications` by product key.

Paging (used by this list, the roles list and the pending-invitations list; `src/AuthService.Api/Common/Paging.cs`, PLT-08):

- Query parameters: `limit` (default `PageQuery.DefaultLimit` = 50, maximum `PageQuery.MaxLimit` = 200; anything outside 1 to 200 is
  `400` with field `limit`: "Limit must be between 1 and 200.").
- Response shape: `{ "items": [...], "next": null | {...} }`. `next` is `null` on the last page.
- Lists ordered by name (members by email, roles by built-in flag then name): the cursor is `{after, afterId}`; pass both back as the
  query parameters `after` and `afterId`. Only one of them gives `400` ("Pass after and afterId together, exactly as the previous page
  returned them."); an empty or over-512-character `after` gives "That is not a cursor this list returned."
- Newest-first lists (pending invitations): the cursor is `{before, beforeId}`, passed back as `before` and `beforeId`, also together.
- The page reads `limit + 1` rows to know whether there is a next page. A cursor holds only a position, never a tenant, so a cursor
  from another tenant just starts somewhere in this tenant's rows.
- The web console reads every page with `AuthApiClient.GetAllAsync`: page size 200, at most 50 pages, so at most 10,000 rows per list.
  **Assumption (confirm):** the console never needs more than that. A bigger list would be silently cut at 10,000.

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `GET /api/auth/tenants/me/members` | Tenant-admin token with `members.read` at `ReadOnly` | `MemberEndpoints.ListAsync` | `MembersService.ListAsync` | One page of members of the token's tenant. |
| `PATCH /api/auth/tenants/me/members/{membershipId:guid}` | Tenant-admin token with `members.manage` at `Full` | `MemberEndpoints.SetStatusAsync` | `MembersService.SetStatusAsync` | Enables or disables a membership. |

Flow of `SetStatusAsync`:

1. `MemberStatusValidator.TryRead` parses the status (never `Invited`). The user id and tenant id come from the token (`sub`, `tid`).
2. Begin a transaction and take a row lock on the tenant: `SELECT 1 FROM tenants WHERE id = {tenantId} FOR UPDATE`. Membership and role
   changes in one tenant therefore take turns, which is what keeps "the tenant keeps an Owner" true when two admins act at once.
3. Load the membership by (`id`, `tenant_id`). Not found: `NotFound` (`404` `not_found`, "No such member in this tenant.").
4. Same status: return `Unchanged`. Target is `Invited` and wanted `Active`: `StillInvited`.
5. Find the tenant's Owner role id (`roles.is_built_in AND name = 'Owner'`).
6. If the member holds the Owner role, whatever their current state and whichever direction, require the caller to be an Active Owner
   (`IsActiveOwnerAsync`; `OwnerRoleRestricted` otherwise). Then, to `Disabled` an *Active* Owner, require another Active Owner
   (`WouldLeaveNoOwner` otherwise).
7. To `Active`: lock each of the member's products' subscription rows in `application_id` order (`FOR UPDATE`, so two re-enablements
   cannot deadlock) and compare `SeatsUsedAsync` with `max_seats`; collect full products into `NoSeatAvailable`.
8. Set `membership.Status`, `audit.Record(MemberDisabled | MemberEnabled, AuditActor.User(caller), tenantId, membershipId)`, save, commit.
9. Reload and return the `MemberItem`.

States and transitions:

```
Invited --accept invitation--> Active <--PATCH Active--> Disabled
   |                                                        ^
   +-------------------PATCH Disabled-----------------------+
(Invited --PATCH Active--> refused 409 membership_invited)
```

Data: `tenant_memberships`, `users` (email, display name, joined for the list), `membership_roles` + `roles` (role names),
`application_access` + `applications` (product keys), `subscriptions` (seat check), `audit_entries`.

Libraries and services: ASP.NET Core minimal APIs, EF Core with Npgsql (row locks through raw `FOR UPDATE` SQL). No external service.

Background workers: none.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `PageQuery.DefaultLimit` / `MaxLimit` | 50 / 200 | `Common/Paging.cs` (constants) | Not configurable. |
| `Auth:Tokens:ProductTokenMinutes` / `Auth__Tokens__ProductTokenMinutes` | 15 | `IdentityTokenOptions` | How long a disabled member's existing tenant-admin token keeps working. |

No other setting.

### Failure modes and production debugging

| Case | Status and `code` |
|---|---|
| Missing `members.read` / `members.manage` Full, or wrong kind of token | `403`, empty body |
| Membership of another tenant, or unknown id | `404` `not_found` |
| `Status` blank, numeric, `Invited`, or unknown | `400` validation problem, field `Status` |
| Invited person enabled | `409` `membership_invited` |
| Non-Owner changes the status of an Owner (disables an Active or Invited one, enables a Disabled one) | `403` `owner_role_restricted` |
| Disabling the last Active Owner | `409` `last_owner` |
| Enabling when a product is full | `409` `no_seat_available` ("...no free seat in: <keys>.") |
| Bad `limit`, half a cursor | `400` validation problem, fields `limit` or `after` |

Audit: `auth.membership.disabled` and `auth.membership.enabled` (`AuditActions.MemberDisabled`, `MemberEnabled`), actor User, `tenant_id`,
detail = the membership id. Nothing is logged by the service itself.

Edge cases and half-states:

- **Expired invitations hold no seat** (AUTH-269, decision 0012). The seat query leaves out an Invited member whose invitation has
  expired, with no worker involved; resending takes the seat back (`409` `no_seat_available` if the product is full by then).
- **Enable of a person who was once Invited and then disabled is allowed** (`Disabled` to `Active`) even though they never accepted.
  See open questions.
- **Role changes and token lifetime.** A disabled member's tokens work until they expire. The console also caches the tenant-admin
  token (`ConsoleTokenCache`, renewed one minute before expiry), so a person can still see console pages for up to about 14 minutes.

Likely causes and how to confirm:

```sql
-- Members of a tenant with status, roles and seats (read-only)
SELECT u.email, m.status, m.id AS membership_id,
       (SELECT string_agg(r.name, ', ') FROM membership_roles mr JOIN roles r ON r.id = mr.role_id WHERE mr.membership_id = m.id) AS roles,
       (SELECT count(*) FROM application_access a WHERE a.membership_id = m.id) AS seats_held
FROM tenant_memberships m JOIN users u ON u.id = m.user_id
WHERE m.tenant_id = '<tenant>' ORDER BY u.email;

-- Who are the Active Owners?
SELECT u.email, m.status
FROM membership_roles mr
JOIN roles r ON r.id = mr.role_id AND r.is_built_in AND r.name = 'Owner'
JOIN tenant_memberships m ON m.id = mr.membership_id
JOIN users u ON u.id = m.user_id
WHERE mr.tenant_id = '<tenant>';

-- Who did what to memberships recently
SELECT occurred_at, action, actor_id, detail FROM audit_entries
WHERE tenant_id = '<tenant>' AND action IN ('auth.membership.disabled','auth.membership.enabled') ORDER BY occurred_at DESC LIMIT 50;
```

| Symptom | Cause | Confirm |
|---|---|---|
| "I cannot disable the owner" | `last_owner` (no other Active Owner) or `owner_role_restricted` (caller is not an Owner) | Owners query above; check the caller's roles |
| "Enable fails with no seat" | A product is at `max_seats`; Active members and unexpired invitations both count | `subscriptions.max_seats` against `SeatsUsedAsync` (count of `application_access` joined to Active memberships and to Invited ones with no expired, unused invitation: `SeatHolders`) |
| Disabled person still works | Token not expired yet, or console cache | Wait up to `ProductTokenMinutes`; check `auth.membership.disabled` time |
| Member list is empty | Token's tenant is not the one expected (`tid`) | Decode the token; list is by `tid` only |

### Decisions and open questions

- `404`, never `403`, for a membership outside the tenant, so ids cannot be probed (comment on `SetMemberStatusResult.NotFound`).
- A tenant-row lock serialises member status changes, role assignment and role deletion per tenant. This is a deliberate trade of throughput for the
  "always an Owner" rule.
- **Fixed (AUTH-266):** the Owner check used to run only when disabling an Active Owner, so a caller with `members.manage` at `Full` who
  was **not** an Owner could re-enable a Disabled Owner or disable an Invited one. It now runs for every status change of an Owner (test
  `TEN07_OnlyAnActiveOwnerCanBringADisabledOwnerBackOrSwitchOffAnInvitedOne`).
- **Inconsistent (finding):** a membership that was Invited, then disabled, can be enabled to `Active` without the person ever
  accepting. For a person who is new to the platform this creates an Active membership for a user with no password. They can still
  reach a password through password reset, which does not look at memberships (`PasswordResetService.RequestAsync`). **Unknown:** whether
  this is intended; `MembersService.SetStatusAsync` and the README ("an invitation can be withdrawn by disabling it") do not say.
- `last_owner` is enforced in code only. There is no database constraint, so manual SQL can leave a tenant with no Owner.
- Page size for the console (200 x 50) is a code constant in `AuthApiClient`.

---

## Invitations

### What it does

An admin invites a person by email. The person gets an email with a link, chooses a password if they are new to the platform (or logs
in if they already have an account), and becomes a member. Admins can see who has not accepted yet and send the link again.

Rules:

- **Inviting.** `POST /api/auth/tenants/me/invitations` with `{"email", "roleIds", "applicationIds"}`; needs `members.invite` at `Full`.
  - `email` is normalised by `EmailNormalizer.Normalize` (trim, lower case) and must be a plausible address
    (`SignupValidator.IsPlausibleEmail`: at most 254 characters, parses as a mail address, host contains a dot).
  - `roleIds`: **1 to 10** distinct role ids of this tenant (`AssignRolesValidator.MaxRolesPerMember` = 10). A role of another tenant, or a
    missing one, is `404`.
  - `applicationIds`: optional, at most **20** (`InviteValidator.MaxApplications`). They give the person product access straight away.
  - **Only an Owner can invite someone with the Owner role** (any built-in role, in practice only Owner): `403` `owner_role_restricted`.
  - An email that already has a membership in this tenant, in **any** status, is `409` `already_member`.
  - Each product must have an **Active** subscription (otherwise `400` `application_not_subscribed`) and a free seat if it has a limit
    (otherwise `409` `no_seat_available`). **An Invited member with access takes a seat** until the invitation expires. One full product refuses the whole invitation
    and nothing is created.
  - The reply (`201`) is the same whether the email was new or already had an account, and never contains the link.
- **A person new to the platform** gets a `users` row with **no password** and no verified email. They cannot log in until they accept
  (the same generic `401` as bad credentials; `LoginService` treats a null `password_hash` as an unknown account).
- **The link** is a single-use token. Only its SHA-256 hash is stored (`invitations.token_hash`, 64 characters). It is valid for
  **7 days** (`Auth:Invitations:TokenDays`).
- **Listing pending invitations.** `GET /api/auth/tenants/me/invitations` needs `members.read` at `ReadOnly`. Newest first, paged by
  `before`/`beforeId` (see Paging above). It lists invitations whose `used_at` is null and whose membership is still `Invited`. Each item has
  `membershipId, email, roles, applicationIds, invitedBy (email), invitedAt, lastSentAt, expiresAt, isExpired`. An expired invitation
  stays in the list, flagged `isExpired`, because that is the one to send again.
- **Resending.** `POST /api/auth/tenants/me/invitations/{membershipId}/resend` needs `members.invite` at `Full`. It creates a **new token on
  the same row** (the old link stops working at once) and a full new expiry, so it also revives an expired invitation, which takes its seats back: if a product it gives access to is full by now it is `409` `no_seat_available`
  and nothing is sent. Within
  **60 seconds** of the last send (`Auth:Invitations:ResendCooldownSeconds`) it is `429` `resend_too_soon` with a `Retry-After` header.
- **Withdrawing.** `DELETE /api/auth/tenants/me/invitations/{membershipId}` needs `members.invite` at `Full` and answers `204`. It deletes the
  pending membership with its roles, access rows and invitation, so the link is `410` at once, the seats are free, and the address can be
  invited again (a membership that stayed would make that `409` `already_member`). A user with no password and no other membership is
  deleted too. It works on an expired invitation. An accepted or never-invited membership is `409` `invitation_not_pending`, another
  tenant's or an unknown one `404`. Audited as `auth.invitation.revoked`.
- **Expiry and seats.** An invitation past `expires_at` (never accepted) holds no seat: `SeatHolders` leaves its membership out of every
  seat count, with no worker (decision 0012). It stays in the list flagged `isExpired`.
- **Accepting.** `POST /api/auth/invitations/accept` with `{"token", "password"}`. No login is needed for a new person.
  - **New person** (the user has no password): a password is required, 8 to 128 characters (`PasswordPolicy`). The email counts as
    verified (`email_verified_at` is set if it was empty) and they can log in at once.
  - **Person who already has an account**: they must be logged in as the invited email (identity token sent with the request): not logged
    in is `401` `login_required`, a different account is `403` `wrong_account`, and sending a password is `400` `password_not_allowed`.
    Accepting never sets or changes a password. Their other memberships are untouched.
  - **A mistake does not use up the link**: those checks run before the link is claimed.
  - An unknown, expired, already used or withdrawn (membership no longer Invited) link is always `410` `invitation_invalid`. The cases
    are deliberately not told apart.
  - Two simultaneous acceptances succeed once (the link is claimed with one conditional update).
  - Accepting makes the membership `Active`. A membership disabled since the invitation was sent stays Disabled and the answer is `410`.
- Roles and product access were attached at invite time. **Accepting does not re-check seats**, because the seat was already counted.

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `POST /api/auth/tenants/me/invitations` | Tenant-admin token, `members.invite` Full | `InvitationEndpoints.InviteAsync` | `InviteUserService.InviteAsync` | Creates user (if new), Invited membership, role links, seats, invitation, queued email. `201`. |
| `GET /api/auth/tenants/me/invitations` | Tenant-admin token, `members.read` ReadOnly | `InvitationEndpoints.ListPendingAsync` | `ManageInvitationsService.ListPendingAsync` | Pending invitations, newest first, paged. |
| `POST /api/auth/tenants/me/invitations/{membershipId:guid}/resend` | Tenant-admin token, `members.invite` Full | `InvitationEndpoints.ResendAsync` | `ManageInvitationsService.ResendAsync` | New token, new expiry, new email. `200`. `409` `no_seat_available` if it had expired and a product is full. |
| `DELETE /api/auth/tenants/me/invitations/{membershipId:guid}` | Tenant-admin token, `members.invite` Full | `InvitationEndpoints.RevokeAsync` | `ManageInvitationsService.RevokeAsync` | Removes the pending membership (roles, access, invitation) and a user that exists only for it. `204`. |
| `POST /api/auth/invitations/accept` | Anonymous (`AllowAnonymous`); an identity token is read if present | `InvitationEndpoints.AcceptAsync` | `AcceptInvitationService.AcceptAsync` | Activates the membership. `200` `{tenantId, membershipId}`. |

Flow of `InviteAsync`:

1. `InviteValidator.TryValidate` checks email, role count and application count (`400` validation problem, fields `Email`,
   `RoleIds`, `ApplicationIds`).
2. Begin a transaction and take `pg_advisory_xact_lock(hashtextextended('invite:<tenant>:<email>', 0))`, so two invitations of the same email
   to the same tenant take turns and the second sees the first.
3. Load the roles in this tenant. Fewer than requested: `RoleNotFound` (`404` `not_found`, "A role was not found in this tenant.").
4. If any chosen role is built-in, the caller must be an Active Owner (`IsActiveOwnerAsync`), otherwise `OwnerRoleRestricted`.
5. Look up the user by email; if a membership exists in this tenant: `AlreadyMember`.
6. For each application id (in order): lock the subscription row `FOR UPDATE`; not Active: `ApplicationNotSubscribed`; at its limit:
   collect for `NoSeatAvailable`.
7. Create the `User` if needed (`Id`, `Email`, `CreatedAt` only), the `TenantMembership` (`Invited`), `MembershipRole` rows and
   `MembershipApplicationAccess` rows.
8. Create the token with `SecretToken.NewValue()`, store `SecretToken.Hash(token)`, `expires_at = now + TokenDays`, `last_sent_at = now`.
9. `outbox.Enqueue(EmailTemplate.Invitation, email, {token, tenantName})` and `audit.Record(InvitationSent, ...)`, then one save and commit.
   The email exists only if the invitation does.

The email is sent later by the background worker (`EmailDispatcher` -> `EmailDeliveryService`). The subject is
"You have been invited to <tenant name>", the link is `{Email:PublicBaseUrl}/accept-invitation?token=<token>`
(`EmailTemplates.Render`, `Link`). The web page `AcceptInvitation.razor` reads the token and posts it to the API.

Flow of `ResendAsync`: lock the membership row (`FOR UPDATE`); not found `NotFound`; if not `Invited` or no unused invitation,
`NotPending`; if `now < last_sent_at + cooldown`, `TooSoon(seconds)`; if the invitation had expired, check each product with a limit under the subscription row lock (`FullProductsAsync`) and
refuse with `NoSeatAvailable`; otherwise replace `token_hash`, `expires_at`, `last_sent_at` on the
same invitation row, enqueue the email, audit, save, commit. Simultaneous resends send one email (the row lock).

Flow of `RevokeAsync`: lock the membership row (`FOR UPDATE`); not found `NotFound`; if not `Invited` or no unused invitation, `NotPending`;
delete the invitations, `membership_roles`, `application_access` and the membership (children first: the foreign keys restrict); delete the
user if it has no password and no other membership; audit `auth.invitation.revoked`; save, commit. Revoke and accept take the same row
lock, so they cannot both win.

Flow of `AcceptAsync`:

1. Hash the token and find the invitation by `token_hash`. Missing, `used_at` set, or `expires_at <= now`: `Gone`.
2. Load the membership; not `Invited`: `Gone`. Load the user; `isNewPerson = password_hash is null`.
3. New person: validate the password (`PasswordPolicy.Validate`) and hash it **before** the transaction. Existing person: require a
   logged-in identity token for the same user and no password.
4. In a transaction, claim the invitation with `UPDATE ... SET used_at = now WHERE id = ... AND used_at IS NULL AND expires_at > now`;
   zero rows: roll back, `Gone`.
5. Activate with `UPDATE ... SET status = 'Active' WHERE id = ... AND status = 'Invited'`; zero rows: roll back, `Gone`.
6. New person: set `password_hash`, and `email_verified_at ??= now`. Audit `InvitationAccepted` (actor = the user), save, commit.

`LoggedInUserAsync` authenticates the JWT bearer scheme manually. A missing, invalid or *product* token (one with an `aud` claim)
counts as "nobody".

Data:

| Table (entity) | Columns |
|---|---|
| `invitations` (`Invitation`) | `id`, `tenant_id`, `membership_id`, `invited_by_user_id`, `token_hash` (unique), `created_at`, `last_sent_at`, `expires_at`, `used_at` |
| `tenant_memberships` | status `Invited` -> `Active` |
| `users` | new row with null `password_hash`; `password_hash` and `email_verified_at` set at acceptance |
| `membership_roles`, `application_access` | rows created at invite time |
| `email_outbox` (`EmailOutboxMessage`) | `to_address`, `template` = `Invitation`, `payload_json` (holds the token until sent), `status`, `attempts`, `next_attempt_at`, `last_error` |

Libraries and services: ASP.NET Core Identity's `IPasswordHasher<User>` (PBKDF2), the email provider configured under `Email`
(Brevo, File or InMemory), PostgreSQL advisory and row locks.

Background workers: `EmailDispatcher` (hosted service). If it is not running (`Email:DispatcherEnabled=false`, or the service is
down) invitations are saved but emails wait in `email_outbox` with status `Pending`. There is no worker for invitations themselves:
nothing expires or deletes them.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Invitations:TokenDays` / `Auth__Invitations__TokenDays` | 7 | `InvitationOptions` | Length of every new or resent link. The email states this value (`EmailLifetime.Describe`). |
| `Auth:Invitations:ResendCooldownSeconds` / `Auth__Invitations__ResendCooldownSeconds` | 60 | `InvitationOptions` | Minimum gap between sends of one invitation. |
| `Email:PublicBaseUrl` / `Email__PublicBaseUrl` | `http://localhost:5080` | `EmailOptions` | The address in the link. Left at the default, links point at localhost. Must be the public web address. |
| `Email:Provider` / `Email__Provider` | unset (Development uses File, Testing InMemory; other environments **must** set it) | `EmailOptions` | Startup fails if unsupported/incomplete (`app.Services.GetRequiredService<IEmailSender>()`). |
| `Email:BrevoApiKey` / `Email__BrevoApiKey` | none; secret | `EmailOptions` | Required for the Brevo provider. Never commit it. |
| `Email:FromAddress`, `Email:FromName` | `no-reply@localhost`, `Auth Service` | `EmailOptions` | Sender shown to invitees. |
| `Email:DispatcherEnabled` | `true` | `EmailOptions` | `false` stops all queued email, including invitations. |
| `Email:DispatchIntervalMilliseconds` | 2000 | `EmailOptions` | Delay between sending queued emails. |
| `Email:MaxAttempts` | 5 | `EmailOptions` | After this many failures the message is `Failed` and its payload (the token) is cleared. |

The retry delays (`Email:RetryDelaySeconds`) and the rest of the `Email` section belong to the email part of this reference (see the README index).

### Failure modes and production debugging

What the caller sees:

| Case | Status and `code` |
|---|---|
| Missing `members.invite`/`members.read`, wrong token type | `403`, empty body |
| Bad email, role count outside 1 to 10, more than 20 products | `400` validation problem, fields `Email`, `RoleIds`, `ApplicationIds` |
| Role of another tenant or unknown | `404` `not_found` |
| Non-Owner invites an Owner | `403` `owner_role_restricted` |
| Email already in this tenant (any status, any case) | `409` `already_member` |
| Product not actively subscribed | `400` `application_not_subscribed` |
| Product full | `409` `no_seat_available` (message lists keys) |
| Resend of an accepted/never-invited membership | `409` `invitation_not_pending` |
| Resend too soon | `429` `resend_too_soon` + `Retry-After` |
| Resend of another tenant's/unknown membership | `404` `not_found` |
| Resend of an expired invitation when a product has filled up | `409` `no_seat_available` (message lists keys) |
| Revoke | `204` |
| Revoke of an accepted/never-invited membership | `409` `invitation_not_pending` |
| Revoke of another tenant's/unknown membership | `404` `not_found` |
| Accept: missing token | `400` validation problem, field `Token` |
| Accept: bad new-person password | `400` validation problem, field `Password`; link still usable |
| Accept: password sent by an existing person | `400` `password_not_allowed` |
| Accept: existing person not logged in | `401` `login_required` |
| Accept: logged in as someone else | `403` `wrong_account` |
| Accept: unknown, expired, used, withdrawn | `410` `invitation_invalid` |

Audit: `auth.invitation.sent`, `auth.invitation.resent` and `auth.invitation.revoked` (actor = the admin, detail = membership id), `auth.invitation.accepted`
(actor = the accepting user, detail = membership id), all with `tenant_id`. No log line is written by the invitation services.
Email delivery logs from `EmailDeliveryService`: "Email {OutboxId} ({Template}) sent after {Attempts} attempt(s).", "... attempt
{Attempts} failed, will retry in {DelaySeconds}s: {Error}", "... failed for good after {Attempts} attempt(s): {Error}", and from
`EmailDispatcher`: "The email dispatcher is disabled; queued emails will wait."

Edge cases and half-states:

- **Expired invitation:** the person stays `Invited` (and a new person cannot log in). The seat is free. Resend revives it and takes the seat
  back; withdrawing removes the membership.
- **Failed email:** after `MaxAttempts` the `email_outbox` row is `Failed` and its token is wiped, so the original link can never be
  recovered. The fix is **resend** (new token, new row), not retrying the old row.
- **Resend cooldown starts when the email is queued** (`last_sent_at = now`), not when it is actually delivered.
- **Person used password reset before accepting:** they now have a password, so `isNewPerson` is false. The link then needs a login
  and rejects a password (`401 login_required`, `400 password_not_allowed`). The web page handles the existing-person path.
  This follows from `AcceptInvitationService`; I did not find a test for it.
- **Spam filters / wrong `PublicBaseUrl`:** the invite exists (`201`) but the person never receives a working link.
- The accept endpoint is anonymous and has no rate limit. Guessing the token is not realistic: `SecretToken.NewValue()` is 256 bits of randomness.

Likely causes and how to confirm:

```sql
-- Invitation state for a person
SELECT i.id, u.email, m.status, i.created_at, i.last_sent_at, i.expires_at, i.used_at, i.expires_at <= now() AS expired
FROM invitations i
JOIN tenant_memberships m ON m.id = i.membership_id
JOIN users u ON u.id = m.user_id
WHERE i.tenant_id = '<tenant>' AND u.email = 'person@example.com';

-- Did the invitation email go out?
SELECT created_at, status, attempts, next_attempt_at, last_error, sent_at
FROM email_outbox WHERE to_address = 'person@example.com' AND template = 'Invitation' ORDER BY created_at DESC;

-- Is the person passwordless (never accepted)?
SELECT email, password_hash IS NULL AS no_password, email_verified_at FROM users WHERE email = 'person@example.com';
```

| Symptom | Cause | Confirm |
|---|---|---|
| "The invitation does not work" (410) | Expired, already used, replaced by a resend, or membership disabled | Invitation query: `used_at`, `expires_at`; `membership.status`; was a newer email sent? |
| No email arrived | Dispatcher off or provider error; wrong sender; spam; or `Failed` | `email_outbox` query; log lines above; `Email:DispatcherEnabled` |
| Link opens localhost | `Email:PublicBaseUrl` not set | Look at the link in the received mail |
| Existing person gets 401 `login_required` | The email already has an account | Expected: they must log in first |
| Existing person gets 403 `wrong_account` | Logged in as another email | Expected |
| Invite says `already_member` for a former member | A Disabled membership still exists | `tenant_memberships` for the user; enable it instead |
| Invite refused with `no_seat_available` | Product at `max_seats`, with Active members and invitations that have not yet expired | The seat query in the Memberships section; withdraw invitations that will not be accepted (an expired one already holds no seat) |
| Resend `429` | Within the cooldown | `Retry-After` header |

### Decisions and open questions

- Inviting an email that has an account in another tenant looks the same as inviting a new one, so one tenant learns nothing about
  others (comment on `InvitationResponse`).
- The link carries the token only in the email; the API never returns it.
- Resend reuses the invitation row, so the list keeps its original `invitedAt` (`created_at`) and shows the new `lastSentAt`.
- **Fixed (AUTH-269, decision 0012):** an expired invitation no longer holds a seat (the seat count leaves it out; resending takes the seat
  back), and `DELETE .../invitations/{membershipId}` withdraws a pending one. The list still shows expired rows, because they can be sent again.
- **Inconsistent:** the invitation `ApplicationIds` limit (20) and role limit (10) are code constants with no setting.
- **Unknown:** what happens to a seat, role and invitation when the invited person is later disabled and the tenant re-enables them
  (see Memberships section). No test covers it.

---

## Roles

### What it does

A role is a named bundle of permissions defined by one organisation. Each permission is granted at a *level*. Admins create, rename,
change and delete roles. Every organisation also has one built-in role, **Owner**, that is created with it and can never be changed or
deleted. A role does not have to be made by hand: a product can ship roles of its own (ONB-04), and the first time an organisation
subscribes to it each becomes an ordinary custom role there (`RoleTemplateCopier`, part 3). It is a copy: nothing marks it, it is edited and
deleted like any other, and one with the same name (case ignored) that the organisation already has is left exactly as it is.

Rules:

- **Levels** (`AccessLevel`, ordered, stored as text in `role_permissions.level`): `None` = 0, `ReadOnly` = 1, `CreateRead` = 2, `Full` = 3.
  A request names a level by word (case-insensitive); numbers are refused ("Level must be None, ReadOnly, CreateRead or Full.").
  `None` is stored but gives no access and is left out of tokens.
- **Highest level wins.** A member with several roles gets, for each permission, the highest level any role grants. There are no deny
  rules (`PermissionResolver`).
- **Name.** Required, trimmed, at most **100** characters (`RoleValidator.MaxNameLength`), unique **per tenant ignoring case**
  (`citext` column and index `ix_roles_tenant_id_name`): `409` `role_name_taken`. So `owner` cannot sit beside the built-in Owner.
  Two tenants can use the same name.
- **Permissions** are optional (a role with none is allowed), at most **500** per role (`RoleValidator.MaxGrantsPerRole`). Each grant is
  `{applicationId, permission, level}`. The key is matched ignoring case (lower-cased). A grant is refused with `400`, and the whole
  request creates or changes nothing, if:
  - the tenant has no **Active** subscription to that product (Suspended and Cancelled do not count) or the product id does not exist:
    "The application is unknown or the tenant has no active subscription to it." (the two cases look the same);
  - the permission key is not in that product's catalog: "Unknown permission '<key>' for this application.";
  - a product and permission appears twice, a field is missing, or the level is invalid.
  - **The Auth Service's own product is always allowed**, without a subscription (`RoleGrantResolver.ResolveAsync`).
- **Updating** (`PATCH /api/auth/roles/{roleId}`): send `name`, `permissions`, or both. Neither is `400` ("Send a new Name, new Permissions, or
  both."). A field left out is left alone. `permissions` replaces the **whole** set (send `[]` to clear it). Only the difference is
  written, so an unchanged grant keeps its row. A rename to another case of the role's own name is allowed.
- **Deleting** (`DELETE /api/auth/roles/{roleId}`): only when **no member holds it** (`409` `role_in_use`; counts disabled and invited
  members too). The role's grants are deleted with it. Returns `204`. The name can be reused afterwards.
- **The Owner role is immutable.** Any `PATCH` or `DELETE` of a role where `is_built_in` is true is `403` `owner_role_immutable`
  ("The built-in Owner role can never be edited, downgraded or deleted."). Why: the Owner role holds Full on every permission of every
  product the tenant uses, computed at token time (nothing is stored); and the rules that "a tenant keeps an Owner" and "only an Owner
  makes an Owner" rely on there being exactly one such role per tenant with a fixed name. There is no endpoint to create another built-in
  role.
- **Changes apply at the member's next token exchange.** A token already issued keeps what it was given until it expires
  (`ProductTokenMinutes`, 15).
- **Listing** (`GET /api/auth/roles`, `roles.read` ReadOnly): `{id, name, isBuiltIn, memberCount, permissions[], createdAt}`, built-in first,
  then by name, paged by `after`/`afterId` (the cursor's `after` is the internal sort key, `"0"+name` for built-in and `"1"+name` for others,
  so treat it as opaque). The Owner lists no permissions. `roles.read` and `roles.manage` are separate: one does not imply the other.

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `POST /api/auth/roles` | Tenant-admin token, `roles.manage` Full | `RoleEndpoints.CreateAsync` | `CreateRoleService.CreateAsync` | Creates a custom role with grants. `201`. |
| `GET /api/auth/roles` | Tenant-admin token, `roles.read` ReadOnly | `RoleEndpoints.ListAsync` | `ManageRolesService.ListAsync` | One page of roles. |
| `PATCH /api/auth/roles/{roleId:guid}` | Tenant-admin token, `roles.manage` Full | `RoleEndpoints.UpdateAsync` | `ManageRolesService.UpdateAsync` | Renames and/or replaces grants. |
| `DELETE /api/auth/roles/{roleId:guid}` | Tenant-admin token, `roles.manage` Full | `RoleEndpoints.DeleteAsync` | `ManageRolesService.DeleteAsync` | Deletes an unused role. `204`. |

Flow of create (ROL-01):

1. `RoleValidator.TryValidate` checks the name and shapes of the grants (`400` with field names such as `Name`, `Permissions[0].Level`).
2. `RoleGrantResolver.ResolveAsync` loads the tenant's Active subscriptions for the product ids named, adds the built-in product id
   if named, loads the catalog rows from `permissions`, and builds errors for each unusable grant.
3. A `Role` (`IsBuiltIn = false`) and one `RolePermission` per grant are added; `audit.Record(RoleCreated, User(caller), tenantId, roleId)`.
4. `SaveChangesAsync`. A unique violation on `ix_roles_tenant_id_name` is turned into `NameTaken` (`409`). There is no lock; the index
   settles races.

Flow of update (ROL-03): begin a transaction; `SELECT * FROM roles WHERE id = ... AND tenant_id = ... FOR UPDATE` (two edits of one role take
turns); not found `NotFound`; built-in `OwnerRoleImmutable`; apply the name; if grants were sent, resolve them as for create, then diff
against `role_permissions` (remove missing, change level, add new); audit `RoleUpdated` (recorded whenever the call is accepted, even if
nothing changed); save and commit; the unique-name violation gives `NameTaken`.

Flow of delete (ROL-04): begin a transaction, lock the tenant row (`FOR UPDATE`) so a role cannot be assigned while it is deleted; not
found `NotFound`; built-in `OwnerRoleImmutable`; any `membership_roles` row `InUse`; delete the grants (`ExecuteDeleteAsync`), remove the
role, audit `RoleDeleted`, save, commit.

Data: `roles`, `role_permissions` (unique on `role_id` + `permission_id`; foreign key includes `tenant_id`; index on `permission_id`),
`permissions` (the product catalog; part 3), `subscriptions`, `applications`, `membership_roles`, `audit_entries`.

How the token uses roles (`PermissionResolver.ResolveAsync`, called by `TokenExchangeService`): if the member holds the built-in Owner
role, every permission key of the **requested product's** catalog is `Full`; then grants from all the member's roles for that product
are merged by highest level. `None` entries are dropped; the result is written into the token as `"key:Level"` strings.

Libraries and services: ASP.NET Core minimal APIs, EF Core with Npgsql (a unique-violation on `ix_roles_tenant_id_name` is recognised through
`Npgsql.PostgresException`). No external service.

Background workers: none.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:ProductTokenMinutes` / `Auth__Tokens__ProductTokenMinutes` | 15 | `IdentityTokenOptions` | How long a role change takes to reach a member who already holds a token. |
| Role name length (100), grants per role (500) | fixed | `RoleValidator` | Not configurable. |

### Failure modes and production debugging

| Case | Status and `code` |
|---|---|
| Missing `roles.manage` Full / `roles.read` ReadOnly | `403`, empty body |
| Missing name, name over 100, invalid grant shape or level, duplicate grant | `400` validation problem |
| Product unknown or not actively subscribed; unknown permission key | `400` validation problem, field `Permissions[n]` |
| Name already used in the tenant (any case) | `409` `role_name_taken` |
| Role of another tenant, or unknown | `404` `not_found` ("No such role in this tenant.") |
| Edit or delete the Owner role | `403` `owner_role_immutable` |
| Delete a role someone holds | `409` `role_in_use` |
| `PATCH` with neither `name` nor `permissions` | `400` validation problem, field `Name` |

Audit: `auth.role.created`, `auth.role.updated`, `auth.role.deleted` (`AuditActions.RoleCreated`, `RoleUpdated`, `RoleDeleted`); actor User,
`tenant_id`, detail = role id (never the name, which could be anything).

Edge cases and half-states:

- **Editing a role whose grant is for a product the tenant no longer actively subscribes to.** `UpdateAsync` re-validates every grant
  in the new `permissions` list. If the client sends back the existing list including the lapsed product's grant, the whole update
  is `400`. The grant has to be dropped, or the subscription made Active again. A rename alone (no `permissions`) is fine.
  This follows from `RoleGrantResolver` and is not covered by a test I found.
- **A lapsed subscription does not remove the stored grants.** Token exchange for that product is refused anyway
  (no Active subscription).
- **Roles missing from the list:** the list is by the token's `tid`; another organisation's roles are never shown.
- **Role changes in a held token:** see the 15-minute window.
- A role deleted while a member's token is alive: the member keeps the permissions in the token until it expires.

Likely causes and how to confirm:

```sql
-- Roles with member counts and grants
SELECT r.name, r.is_built_in,
       (SELECT count(*) FROM membership_roles mr WHERE mr.role_id = r.id) AS holders,
       (SELECT string_agg(a.key || ':' || p.key || '=' || rp.level, ', ')
          FROM role_permissions rp JOIN permissions p ON p.id = rp.permission_id JOIN applications a ON a.id = p.application_id
         WHERE rp.role_id = r.id) AS grants
FROM roles r WHERE r.tenant_id = '<tenant>' ORDER BY r.is_built_in DESC, r.name;

-- Role audit trail
SELECT occurred_at, action, actor_id, detail FROM audit_entries
WHERE tenant_id = '<tenant>' AND action LIKE 'auth.role.%' ORDER BY occurred_at DESC LIMIT 50;
```

### Decisions and open questions

- Grants are stored per permission **id**, so they survive a product re-registering and relabelling its catalog (`RolePermission` comment).
- Owner holds "everything" by rule, not by stored rows, so a permission added to a catalog later is covered at once
  (`PermissionResolver`). The cost is that the Owner role cannot be inspected or limited per product.
- The unique name is case-insensitive on purpose so no role can pass for the built-in Owner.
- **Risk:** `IsBuiltIn` plus the name `Owner` identifies the Owner everywhere (`Role.OwnerName`). `InviteUserService` treats *any*
  built-in role as Owner-only; `MembersService` and `AssignRolesService` look for `is_built_in AND name = 'Owner'`. This is consistent
  today because only one built-in role exists.
- **Unfinished / open:** there is no way to see which members hold a role from the role endpoints, only `memberCount`.
- **Assumption (confirm):** audit `RoleUpdated` being recorded on a no-op update (a rename to the same name) is intended: the comment says
  "Audited whenever the call is accepted".

---

## Assigning roles to members

### What it does

An admin decides which roles each person holds in the organisation. Setting roles replaces the whole set at once. Roles decide what a
person can do inside a product, but not whether they can enter it (that is product access, below).

Rules:

- `PUT /api/auth/tenants/me/members/{membershipId}/roles` with `{"roleIds": ["...", "..."]}`; needs `roles.assign` at `Full`.
- **1 to 10 roles** (`AssignRolesValidator.MaxRolesPerMember`). `roleIds` is required (`400`), a nil Guid is `400`, more than 10 is `400`
  ("A member can hold at most 10 roles."). The same role twice counts once. Zero roles is `409` `roles_required` ("...To remove their access,
  disable the membership instead."). A failed call leaves the old roles.
- **404 for anything outside the tenant:** a membership or role of another tenant, or one that does not exist, all give the same `not_found`.
- **The Owner role is protected.** If the change gives or removes the Owner role, the caller must be an Active Owner
  (`403` `owner_role_restricted`: "Only an Owner can give or remove the Owner role."). Taking the role from someone must leave at least one
  **other Active** Owner (`409` `last_owner`). A Disabled or Invited Owner does not count as an Owner here.
- **Roles never change seats.** Product access is separate (SUB-05).
- **Applies at the next token exchange** of the member.
- Works on a membership in any status (Invited, Active, Disabled).

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `PUT /api/auth/tenants/me/members/{membershipId:guid}/roles` | Tenant-admin token, `roles.assign` Full | `RoleEndpoints.AssignAsync` | `AssignRolesService.AssignAsync` | Replaces the member's role set. `200` `{membershipId, roles[{id,name,isBuiltIn}]}`. |

Flow:

1. `AssignRolesValidator.TryValidate` (distinct ids, 0 to 10, no nil Guid).
2. Begin a transaction and lock the tenant row `FOR UPDATE` (the same lock as member status changes and role deletion).
3. Membership not in this tenant: `NotFound`. Fewer roles found in this tenant than requested: `NotFound`. Zero roles: `EmptySet`.
4. Load the Owner role id and the member's current assignments. If the member had Owner and the new set does not, or vice versa
   (`hadOwner != wantsOwner`): the caller must be an Active Owner, else `OwnerRoleRestricted`. If taking it away: another Active Owner
   must exist, else `WouldLeaveNoOwner`.
5. Remove assignments not in the new set; add those missing (`MembershipRole`, `Id = Guid.CreateVersion7()`); an unchanged role keeps its row.
6. `audit.Record(MemberRolesChanged, User(caller), tenantId, membershipId)`; save; commit.

Data: `membership_roles` (unique on `membership_id` + `role_id`), `roles`, `tenant_memberships`, `audit_entries`.

Libraries and services: ASP.NET Core minimal APIs, EF Core with Npgsql. No external service. Background workers: none.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:ProductTokenMinutes` / `Auth__Tokens__ProductTokenMinutes` | 15 | `IdentityTokenOptions` | Delay before the new roles reach a token already issued. |
| Max roles per member | 10 | `AssignRolesValidator` constant | Not configurable. |

### Failure modes and production debugging

| Case | Status and `code` |
|---|---|
| Missing `roles.assign` Full | `403`, empty body |
| `roleIds` missing, nil Guid, or over 10 | `400` validation problem, field `RoleIds` |
| Empty set | `409` `roles_required` |
| Membership or any role not in this tenant | `404` `not_found` |
| Non-Owner gives or removes Owner | `403` `owner_role_restricted` |
| Would leave no Active Owner | `409` `last_owner` |

Audit: `auth.membership.roles_changed` (`AuditActions.MemberRolesChanged`), detail = membership id. A repeated identical request still writes
an audit entry but changes no rows (test `ROL05_RepeatingTheSameRequestChangesNothingFurther`).

Edge cases and half-states:

- **"The member lost access after a role change"** is usually the token lifetime working as designed: the old token keeps working for up
  to 15 minutes, then the new one has the new set. If a member is *missing* a permission they should have, check their roles and the
  level (`Full` is needed for every write endpoint).
- **Holding no role at all is impossible through the API** (the empty set is refused), but possible by manual SQL.
- The Owner's `application_access` rows are not touched by role changes. Removing Owner from a person does not remove their seats.

Likely causes and how to confirm:

```sql
SELECT r.name, mr.assigned_at FROM membership_roles mr JOIN roles r ON r.id = mr.role_id
WHERE mr.membership_id = '<membership>' ORDER BY r.name;

SELECT occurred_at, actor_id, detail FROM audit_entries
WHERE tenant_id = '<tenant>' AND action = 'auth.membership.roles_changed' ORDER BY occurred_at DESC LIMIT 20;
```

### Decisions and open questions

- Whole-set replacement (not add/remove) makes concurrent edits last-writer-wins, but the tenant lock stops them interleaving.
- "Only an Owner can give or take the Owner role" exists because anyone allowed to assign roles could otherwise make themselves an Owner.
- Removing the Owner role from a *Disabled* Owner still needs another Active Owner (the check only looks at other Active Owners, test
  `ROL05_ADisabledOwnerDoesNotCountAsAnOwnerWhenTheLastActiveOneStepsDown`).
- Role assignment and status changes now both protect Owners (see the Memberships section).

---

## The permission catalog and tenant-admin permissions

### What it does

The Auth Service is registered as a product of its own (the *built-in product*), with its own list of permissions. An organisation
administrator uses the same token mechanism as for any product, trading their login for a token for this built-in product, and the
Auth Service checks that token's permissions on every admin endpoint. The *permission catalog* endpoint lists what a role could grant,
so a role-building screen offers only choices that will be accepted.

Rules:

- **Built-in product identity** (`BuiltInApplication`): id `00000000-0000-7000-8000-00000000a07a`, key `auth-service`, name `Auth Service`,
  audience (`aud`) `auth-service-tenant`, client id `cl_builtin_auth_service` (unusable, no secret). It is stored at startup by
  `BuiltInApplicationSync` (add-only for permissions), is not listed to tenants (`GET /api/auth/applications`), and the admin API treats
  it as not found.
- **No subscription and no seat are needed.** Any Active member of a tenant can get a tenant-admin token. What they can do depends only
  on their roles. The Owner role holds `Full` on the whole built-in catalog.
- **The catalog** (`BuiltInApplication.Permissions`, `PermissionKeys`):

| Key | Label | Required by (minimum level) |
|---|---|---|
| `subscriptions.read` | View subscriptions | `GET /api/auth/tenants/me/subscriptions` (ReadOnly) |
| `audit.read` | View the audit log | `GET /api/auth/tenants/me/audit` (ReadOnly) |
| `apikeys.read` | View API keys | `GET /api/auth/apikeys`, `GET /api/auth/apikeys/{keyId}` (ReadOnly) |
| `apikeys.manage` | Manage API keys | `POST /api/auth/apikeys`, `DELETE /api/auth/apikeys/{keyId}`, `POST /api/auth/apikeys/{keyId}/rotate` (Full) |
| `members.read` | View members | `GET /api/auth/tenants/me/members`, `GET /api/auth/tenants/me/invitations` (ReadOnly) |
| `members.manage` | Manage members | `PATCH /api/auth/tenants/me/members/{membershipId}` (Full) |
| `members.invite` | Invite members | `POST /api/auth/tenants/me/invitations`, `POST .../invitations/{membershipId}/resend`, `DELETE .../invitations/{membershipId}` (Full) |
| `roles.read` | View roles | `GET /api/auth/roles`, `GET /api/auth/tenants/me/permissions` (ReadOnly) |
| `roles.manage` | Manage roles | `POST`, `PATCH`, `DELETE` on `/api/auth/roles` (Full) |
| `roles.assign` | Assign roles | `PUT /api/auth/tenants/me/members/{membershipId}/roles` (Full) |
| `access.manage` | Manage product access | `PUT` / `DELETE /api/auth/tenants/me/members/{membershipId}/applications/{applicationId}` (Full) |

  `GET /api/auth/tenant/me` needs only a valid tenant-admin token (`RequireTenantContext()`).
- **How a permission is checked** (`PermissionHandler`): the token's `permissions` claim is an array of `key:Level` strings. The handler
  splits on the **last** colon, compares the key exactly (ordinal, case-sensitive), parses the level (case-sensitive, must be a defined
  value) and succeeds if the level is at least the required one. It never looks at role names.
- **How a tenant-admin token is obtained:**
  1. Log in (`POST /api/auth/login`) to get an identity token (no `aud`, 15 minutes: `Auth:Tokens:AccessTokenMinutes`).
  2. `POST /api/auth/token/exchange` with `{"tenantId": "<tenant>", "applicationId": "00000000-0000-7000-8000-00000000a07a"}`.
  3. The reply `{accessToken, tokenType: "Bearer", expiresIn: 900}` is sent as `Authorization: Bearer ...`. Its `aud` is
     `auth-service-tenant`, `tid` is the tenant, `sub` the person, `permissions` what their roles allow here.
  - Exchange needs an **Active** membership in a tenant that is not suspended; it is rate limited per signed-in person
    (`Auth:TokenExchange:UserLimit` = 30 per `UserWindowMinutes` = 1, then `429` `rate_limited`). The web console does exactly this in
    `ConsoleSession.OpenAsync` and caches the token (`ConsoleTokenCache`) until one minute before it expires.
  - The token is refused for a service token, an identity token or another product's token (`403`).
- **Reading the catalog.** `GET /api/auth/tenants/me/permissions` (`roles.read` ReadOnly) returns
  `{levels: ["ReadOnly","CreateRead","Full"], applications: [{applicationId, key, name, isBuiltIn, permissions: [{key, label, description}]}]}`:
  the built-in product first, then every product with an **Active** subscription, by name. `None` is not listed (it grants nothing). It is the
  exact set a role grant is accepted for. It is not paged.

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `GET /api/auth/tenants/me/permissions` | Tenant-admin token, `roles.read` ReadOnly | `TenantPermissionCatalogEndpoints.GetAsync` | direct `AuthDbContext` read | Lists grantable permissions for the token's tenant. |
| `POST /api/auth/token/exchange` | Identity token | `TokenExchangeEndpoints.ExchangeAsync` | `TokenExchangeService.ExchangeAsync`, `PermissionResolver.ResolveAsync` | Issues the tenant-admin token (documented in the tokens part). |

Wiring:

- `src/AuthService.Api/Tokens/TenantPermissions.cs`: `TenantAuthorization.TenantContext` requires an authenticated user, claim `aud` =
  `BuiltInApplication.Audience`, a `tid` claim that parses as a Guid, and a `sub` claim. `TenantPermission` adds a `PermissionRequirement(key,
  minimumLevel)`. `RequireTenantPermission(key, level)` and `RequireTenantContext()` are the endpoint extension methods.
- `IdentityAuthentication.AddIdentityTokenAuthentication` registers JWT bearer validation (ES256 only, issuer checked, audience **not**
  validated by the bearer handler, 30-second clock skew) and `PermissionHandler` as a singleton `IAuthorizationHandler`. The audience is
  enforced by the policy.
- `Program.cs` calls `BuiltInApplicationSync.SyncAsync` before serving, inside an advisory lock (`pg_advisory_xact_lock`), so two instances
  starting together take turns. It creates the application row if missing, refreshes name/audience/`is_active`, and **adds or updates**
  permissions (label, description). A permission removed from code stays in the database, because a role may still grant it. If the key
  `auth-service` is registered to a different application, **startup throws**: "The application key 'auth-service' is reserved for the Auth
  Service itself but is registered to another application."
- All tenant endpoints read the tenant as `caller.GetTenantId()` (the `tid` claim). Nothing reads a tenant id from the route, query or body.
- To add a new tenant endpoint: add a key to `PermissionKeys` and a `BuiltInPermission` to `BuiltInApplication.Permissions`, and use
  `RequireTenantPermission`. A new key appears on the next deployment (sync at startup); the Owner gets it immediately.

Data: `applications` (built-in row with `is_built_in = true`), `permissions` (catalog rows, `application_id` = built-in id), `subscriptions`.

Libraries and services: `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.IdentityModel.*` (ES256 tokens), ASP.NET Core authorization
policies, EF Core with Npgsql. No external service. Background workers: none at request time; the catalog is synced once at startup, and
`SigningKeyWorker` (hosted service, tokens part) rotates the signing keys the tokens are validated against.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:ProductTokenMinutes` / `Auth__Tokens__ProductTokenMinutes` | 15 | `IdentityTokenOptions` | Lifetime of the tenant-admin token and how stale its permissions can be. |
| `Auth:Tokens:Issuer` / `Auth__Tokens__Issuer` | `AuthService` | `IdentityTokenOptions` | A mismatch between issuing and validating instances gives `401` on every call. |
| `Auth:TokenExchange:UserLimit` / `Auth__TokenExchange__UserLimit` | 30 | `TokenExchangeOptions` | Exchanges per person per window before `429`. |
| `Auth:TokenExchange:UserWindowMinutes` / `Auth__TokenExchange__UserWindowMinutes` | 1 | `TokenExchangeOptions` | Window length. |
| Signing keys (`SigningKeyService`) | n/a | tokens part of this reference | A missing key-encryption secret or an unreachable database stops startup (comment in `Program.cs`). |

The catalog itself is in code; there is no setting.

### Failure modes and production debugging

| Case | Status and `code` |
|---|---|
| No token, bad token | `401` `unauthorized` |
| Identity token, other product's token, service token on a tenant endpoint | `403`, empty body |
| Tenant-admin token without the needed permission or level | `403`, empty body |
| Exchange: not an Active member, no such tenant | `403` `exchange_denied` |
| Exchange: Active member of a suspended tenant | `403` `tenant_suspended` |
| Any tenant-admin call with a token of a suspended tenant | `401` `tenant_suspended` |
| Accepting an invitation into a suspended tenant | `403` `tenant_suspended` (the link is not used up) |
| `PUT /api/auth/admin/tenants/{id}/status`: bad status or a reason that is not a listed code | `400` validation problem, fields `Status`, `Reason` |
| `PUT /api/auth/admin/tenants/{id}/status`: unknown organisation | `404` `tenant_not_found` |
| Exchange: too many in a minute | `429` `rate_limited` with `Retry-After` |
| Startup: `auth-service` key taken by another application | process exits with the `InvalidOperationException` above |

Distinguishing `403` types: a `403` from a *permission* failure has **no body and no `code`** (the framework's default response; I found
no custom handler). A `403` with a `code` such as `owner_role_restricted` comes from the business rule in a service.

What is logged: nothing specific; failed authorization is not audited. Check the web console's own page text: "You can't see roles" etc.
means the cached token lacks the permission (page gates use `ConsoleContext.Can`, read from the token's claims without verifying them;
the API still checks each call).

Edge cases and half-states:

- **Console pages and the API disagree for up to ~14 minutes** after a role change, because the console caches the token.
  `ConsoleSession.Refresh` drops it after the API answers `401`.
- A person whose role lacks a *secondary* permission cannot use a page that needs several. Examples (`Components/Pages/Console`): the
  Roles page needs `roles.read`; the member-roles page needs `roles.assign` Full **and** `roles.read` **and** `members.read`; the
  Invitations page needs `members.read` (and `roles.read` to invite, `subscriptions.read` to offer products); the Product access page needs
  `access.manage` ReadOnly **and** `subscriptions.read` **and** `members.read`.
- Permissions are matched by exact key. A typo in a custom product's permission is "Unknown permission" at role creation.
- The built-in product is never listed to the admin API, so an operator cannot "fix" it through that API.

Likely causes and how to confirm:

```sql
-- Is the built-in product and its catalog in the database?
SELECT a.key, a.is_active, a.is_built_in, a.jwt_audience, count(p.id) AS permissions
FROM applications a LEFT JOIN permissions p ON p.application_id = a.id
WHERE a.is_built_in GROUP BY a.key, a.is_active, a.is_built_in, a.jwt_audience;
```

To see what a person's token holds, decode the JWT payload (do not paste a token into a chat or ticket) and read `permissions`.

### Decisions and open questions

- Decision 0005: the Auth Service protects itself as a product. The alternatives (an interim header-based guard, or a second token type)
  were rejected so every endpoint reads the tenant from `tid` and checks `permissions` by level.
- The catalog lives in code and is synced add-only, so deployments carry new permissions with the endpoints that use them.
- **Risk:** a permission removed from `BuiltInApplication.Permissions` stays in the `permissions` table and can still appear in the
  catalog endpoint and be granted. Nothing removes it.
- **Risk:** `ConsoleContext.Can` reads the token payload without verifying the signature. It only decides what to show; the API enforces. This
  is stated in `ProductTokenClaims` ("The API checks the token on every call; this only decides what to show").

---

## Member product access (seats)

### What it does

For a product the organisation subscribes to, a person also needs a *seat*, a row in `application_access`, to be allowed in. Roles then
decide what they may do there. An admin gives or takes away a seat for a member. This section covers the membership side; subscriptions,
seat limits and the product's view are in [part 3](03-products-and-subscriptions.md).

Rules (membership-related):

- `PUT /api/auth/tenants/me/members/{membershipId}/applications/{applicationId}` gives access; `DELETE` on the same path takes it away. Both
  need `access.manage` at `Full`.
- **Granting** needs an **Active** subscription to the product (`400` `application_not_subscribed`; an unknown, unsubscribed, suspended
  product or the Auth Service's own all give this answer), a membership that is **not Disabled** (`409` `membership_disabled`, "Enable
  the membership first.") and a free seat if `max_seats` is set (`409` `no_seat_available`). An Invited member can be given access and takes a
  seat. Granting what the member already has returns `200` and changes nothing; a new seat returns `201` with
  `{membershipId, applicationId, hasAccess, seatsUsed, maxSeats}`.
- **Revoking** is always allowed, even on a suspended subscription, so seats can be freed. It returns `204`, also when there was nothing
  to revoke.
- A membership of another tenant, or an unknown one, is `404`.
- **A seat is counted for Active members and for Invited ones whose invitation has not expired; Disabled members and expired invitations hold no seat**
  (`SeatsUsedAsync`, `SeatHolders`; decision 0012).
- Access applies at the member's next token exchange: an exchange for a non-built-in product is refused (`exchange_denied`) without an
  access row, whatever the roles.
- **The tenant's Owner gets a seat automatically** when the tenant subscribes (`SubscribeTenantService.GrantOwnersAccessAsync`, for every
  non-Disabled Owner). Role changes never change seats.
- **Membership changes and seats:** disabling a member frees their seats, enabling re-checks the limits, inviting with products takes
  seats (see the earlier sections).

### How it is implemented

| Verb and route | Auth required | Handler | Service / method | What it does |
|---|---|---|---|---|
| `PUT /api/auth/tenants/me/members/{membershipId:guid}/applications/{applicationId:guid}` | Tenant-admin token, `access.manage` Full | `MemberAccessEndpoints.GrantAsync` | `MemberAccessService.GrantAsync` | Adds an `application_access` row if a seat is free. |
| `DELETE` same route | Tenant-admin token, `access.manage` Full | `MemberAccessEndpoints.RevokeAsync` | `MemberAccessService.RevokeAsync` | Removes the row. |

Flow of grant: begin a transaction; load the membership in the token's tenant (`MembershipNotFound`); lock the subscription row `FOR UPDATE`
(this is what makes the count safe: grants and limit changes take turns); not Active `NotSubscribed`; membership Disabled
`MembershipDisabled`; compute `SeatsUsedAsync`; already has access: `AlreadyGranted`; at the limit: `NoSeatAvailable`; add the row,
audit `MemberAccessGranted` with detail `"{membershipId}.{productKey}"`, save, commit.

Flow of revoke: membership must exist in the tenant; take the same subscription lock; remove the row if present; audit
`MemberAccessRevoked`.

Data: `application_access` (`MembershipApplicationAccess`: `id`, `tenant_id`, `membership_id`, `application_id`, `granted_at`; unique on
`membership_id` + `application_id`; index on `tenant_id` + `application_id`), `subscriptions` (`max_seats`, `status`), `tenant_memberships`.

Libraries and services: ASP.NET Core minimal APIs, EF Core with Npgsql (`SELECT ... FOR UPDATE` on the subscription row). No external
service. Background workers: none.

### Configuration and environment

| Setting | Default | Defined in | What changes if it differs |
|---|---|---|---|
| `Auth:Tokens:ProductTokenMinutes` / `Auth__Tokens__ProductTokenMinutes` | 15 | `IdentityTokenOptions` | How long a revoked seat keeps working in an already-issued product token. |
| Seat limit per tenant and product | per subscription (`subscriptions.max_seats`, null = unlimited) | set by the product (SUB-04) | See part 3. |

### Failure modes and production debugging

| Case | Status and `code` |
|---|---|
| Missing `access.manage` Full | `403`, empty body |
| Product not actively subscribed | `400` `application_not_subscribed` |
| Disabled membership | `409` `membership_disabled` |
| No free seat | `409` `no_seat_available` |
| Membership not in the tenant | `404` `not_found` |
| Revoke when no seat | `204` (no-op) |

Audit: `auth.membership.access_granted` and `auth.membership.access_revoked`, detail `"<membership-id>.<product-key>"`.

Likely causes and how to confirm:

```sql
-- Seats used vs limit for a tenant's products
SELECT a.key, s.status, s.max_seats,
       (SELECT count(*) FROM application_access x JOIN tenant_memberships m ON m.id = x.membership_id
         WHERE x.tenant_id = s.tenant_id AND x.application_id = s.application_id
           AND (m.status = 'Active' OR (m.status = 'Invited' AND NOT EXISTS (
                 SELECT 1 FROM invitations i WHERE i.membership_id = m.id AND i.used_at IS NULL AND i.expires_at <= now())))) AS seats_used
FROM subscriptions s JOIN applications a ON a.id = s.application_id WHERE s.tenant_id = '<tenant>';

-- Who holds a seat in a product
SELECT u.email, m.status, x.granted_at FROM application_access x
JOIN tenant_memberships m ON m.id = x.membership_id JOIN users u ON u.id = m.user_id
WHERE x.tenant_id = '<tenant>' AND x.application_id = '<application>' ORDER BY u.email;
```

| Symptom | Cause | Confirm |
|---|---|---|
| Member has roles but exchange says `exchange_denied` | No `application_access` row, or no Active subscription | The two queries above |
| Seat count looks too high | Invited members with a live invitation count (expired ones do not) | `m.status = 'Invited'` rows in the second query; `invitations.expires_at` |
| Seat limit lowered below usage | Existing members keep access; only new grants blocked | Part 3 |

### Decisions and open questions

- Access (can enter) and roles (what they can do) are separate on purpose; roles never move seats.
- The subscription-row lock is the single serialisation point for seat checks (grants, invites, re-enabling, limit changes).
- **Inconsistent (finding):** `MemberAccessService.GrantAsync` does not check the tenant status; a Suspended tenant can still hand out
  seats with an unexpired tenant-admin token.
- **Risk:** the grant endpoint does not lock the tenant row, while member status changes and role changes do. They only share the
  subscription lock when enabling; I found no sequence that breaks the seat count.

---

## Symptom to where to look (this part)

| Symptom a user or on-call engineer reports | Likely cause | First place to look (file and method, setting, table) | What to check |
|---|---|---|---|
| "I cannot disable the owner" (409 `last_owner`) | Target is the only Active Owner | `MembersService.SetStatusAsync`; `roles` + `membership_roles` | Owners query; make another Active Owner first (`PUT .../roles`) |
| "I cannot disable the owner" (403 `owner_role_restricted`) | Caller is not an Active Owner | `MembersService.IsActiveOwnerAsync` | Caller's roles and membership status |
| "The invitation does not work" (410 `invitation_invalid`) | Expired (after `Auth:Invitations:TokenDays`), already used, replaced by a resend, or membership disabled | `AcceptInvitationService.AcceptAsync`; `invitations` | `used_at`, `expires_at`, `membership.status`; resend a fresh link |
| Invited person never got the email | Email worker off, provider error, wrong sender, or message `Failed` | `EmailDeliveryService`; `email_outbox`; `Email:DispatcherEnabled`, `Email:Provider` | `status`, `attempts`, `last_error`; logs "Email ... failed" |
| Invitation link opens `localhost` | `Email:PublicBaseUrl` not set | `EmailTemplates.Link`; env `Email__PublicBaseUrl` | The link in the received mail |
| Existing user accepting gets `login_required` or `wrong_account` | They must be logged in as the invited email | `InvitationEndpoints.AcceptAsync`, `LoggedInUserAsync` | Which account is signed in; a product token counts as nobody |
| Invite says `no_seat_available` though few people work there | Pending (unexpired) invitations hold seats | `InviteUserService`; `SubscriptionResponses.SeatHolders` | Seat query; withdraw invitations that will not be accepted |
| Invite refused `already_member` | A membership exists in any status, including Disabled | `InviteUserService.InviteAsync` | `tenant_memberships` for the user; enable instead of inviting |
| Member lost access (or still has access) after a role or status change | Token lifetime: old token valid until `Auth:Tokens:ProductTokenMinutes` (15); console caches it too | `TokenExchangeService`, `ConsoleTokenCache` | Wait 15 minutes; confirm in `membership_roles`/`tenant_memberships`; audit actions |
| Roles not appearing on the Roles page | Wrong tenant in the token, missing `roles.read`, or the page cut at 10,000 | `ManageRolesService.ListAsync`; `AuthApiClient.GetAllAsync` | Roles query; token `tid` and `permissions` |
| New permission not in the catalog or not grantable | Not in `BuiltInApplication.Permissions`, deployment not run, or product not Active-subscribed | `BuiltInApplicationSync`, `RoleGrantResolver` | Catalog query; subscription status |
| Role create/edit returns `400` "no active subscription" | Product subscription lapsed (also when editing an old role that still holds that grant) | `RoleGrantResolver.ResolveAsync` | Subscription status; drop the grant or resubscribe |
| 403 on a console page | Missing permission or level in the tenant-admin token (e.g. needs `Full` to change) | `PermissionHandler`; `ConsoleContext.Can`; page file in `Components/Pages/Console` | Decode token `permissions`; check role grants; 403 with empty body means permission |
| Console says "You don't have access" | Membership not Active, or exchange refused | `ConsoleSession.OpenAsync`, `ConsoleDenied.razor` | `/api/auth/users/me` memberships; `tenant_memberships.status` |
| Suspended organisation cannot open its console | `tenants.status = 'Suspended'`, exchange returns `tenant_suspended` | `TokenExchangeService`, `ActiveTenantHandler`; `tenants` | `SELECT status FROM tenants`; the audit log for who and why; reactivate in the platform console (Organisations) |
| Delete role fails `role_in_use` | Someone (even a disabled member) still holds it | `ManageRolesService.DeleteAsync`; `membership_roles` | Roles query `holders` column |
| Cannot rename or delete the Owner role | By design | `ManageRolesService` (`OwnerRoleImmutable`) | None; create a custom role instead |
| Creating an organisation returns `account_disabled` | Every membership of the person is Disabled | `CreateTenantService`; `AccountAccess.IsDisabled` | `tenant_memberships` for the user |
| Resend returns 429 `resend_too_soon` | Within `Auth:Invitations:ResendCooldownSeconds` of the last send | `ManageInvitationsService.ResendAsync` | `Retry-After` header; `invitations.last_sent_at` |
| Every tenant admin call returns 401 | Token invalid: signature/issuer/expiry, signing keys, clock | `IdentityAuthentication`; `Auth:Tokens:Issuer`; JWKS | Token `exp`, `iss`; server clock; the tokens part of this reference |

## Highest-risk areas in this part

1. **Fixed in AUTH-268: tenant suspension is no longer manual and leaky.** Platform admins suspend and reactivate through the API and the console,
   each change is audited with a reason code, and a suspended organisation is refused by every tenant-admin call (checked per request),
   token exchange, invitation acceptance, subscribing, seat-limit changes and API-key validation (decision 0013). What remains: every tenant-admin
   request now reads `tenants` once, and products are not notified.
2. **The "tenant keeps an Owner" rule is code-only.** There is no database constraint. Review `MembersService.SetStatusAsync` and `AssignRolesService.AssignAsync`.
3. **Fixed in AUTH-269: unaccepted invitations no longer hold seats for ever.** An expired invitation leaves the seat count and any pending
   one can be withdrawn (`DELETE .../invitations/{membershipId}`); decision 0012. What remains: an unexpired invitation to someone who will
   never accept holds its seat for up to `Auth:Invitations:TokenDays` unless an admin withdraws it, and resending an expired one is refused
   when the product is full.
4. **Email delivery is the single path to a working invitation.** A dispatcher that is off, a `Failed` row (token cleared), or a wrong
   `Email:PublicBaseUrl` all produce "the invitation does not work". Review `EmailTemplates`,
   `EmailDeliveryService`, and the deployment settings.
5. **Permission changes lag by the token lifetime plus the console cache.** Disabling a person or lowering a role does not take effect for up
   to 15 minutes (`Auth:Tokens:ProductTokenMinutes`) and about 14 minutes more in the console cache. Review whether that is acceptable for
   disabling a compromised admin.
6. **Role editing re-validates every grant against current subscriptions.** A role with a lapsed product grant cannot be edited without
   dropping that grant. Review `RoleGrantResolver.ResolveAsync` and `ManageRolesService.UpdateAsync`.

## Unknowns and assumptions to confirm

- **Assumption (confirm):** suspension and reactivation of an organisation are meant to be done by an operator directly in the database
  for now (no endpoint exists; tests set the status in SQL).
- **Assumption (confirm):** unlimited organisations per person, with no rate limit on `POST /api/auth/tenants`, is intended.
- **Assumption (confirm):** the web console never needs more than 10,000 rows per list (`AuthApiClient.GetAllAsync`: 200 x 50).
- **Assumption (confirm):** "only an Owner changes Owners" was meant to cover enabling a Disabled Owner and disabling an Invited Owner
  (`MembersService.SetStatusAsync` checks Owners only when disabling an Active one).
- **Unknown:** whether enabling a membership that was Invited and then disabled (so the person never accepted) is intended. Settled by
  the owner's decision; code in `MembersService.SetStatusAsync`.
- **Assumption (confirm):** a no-op role update (same name) being audited as `auth.role.updated` is intended.
- **Unknown:** behaviour of an invited, never-accepted person who has set a password through password reset (the accept link then
  demands a login). Derived from code; no test found.
- **Finding, not an assumption:** the invitation email says "7 days" regardless of `Auth:Invitations:TokenDays`.
