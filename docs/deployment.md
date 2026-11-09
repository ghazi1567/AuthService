# Deploying AuthService

The service ships as three container images built from the one `Dockerfile`. Every release (a Git tag `v1.0.0`) publishes them to GitHub's
container registry, for Intel and ARM servers, so **a server runs a release without ever having the source code**. They run on anything that
runs containers (a VPS with Docker, Railway, Render, Fly, Azure Container Apps, Kubernetes): the host decides how, this page says what. **The
quickest way to a working system is the next section: one Ubuntu server, Docker Compose, HTTPS handled for you.**

| Image | Published as | What it does |
|---|---|---|
| API | `ghcr.io/ghazi1567/authservice-api:<version>` | The web service. Listens on port **8080**, runs as a non-root user. |
| Web console | `ghcr.io/ghazi1567/authservice-web:<version>` | The pages people sign in on (sign-up, sign-in, the tenant console, the platform console). Listens on **8080**, runs as a non-root user, calls the API from its server. |
| Migrate | `ghcr.io/ghazi1567/authservice-migrate:<version>` | A one-off job: applies every pending database migration, then exits. |

To build them yourself instead: `docker build -t authservice-api .`, `docker build --target web -t authservice-web .` and
`docker build --target migrate -t authservice-migrate .`, or use `deploy/docker-compose.build.yml` (below).

The API **never migrates on startup** ([decision 0003](decisions/0003-migrations-applied-explicitly.md)). Every release runs the migrate
job first, then starts the new API.

`deploy/docker-compose.yml` is a worked example for a single machine: the HTTPS proxy (Caddy), the web console, the API, PostgreSQL and
the migrate job. It pulls the images from the registry. It was run for real while writing this page.

**Before the first release exists**, nothing is in the registry yet: tag one (see "Publishing a release") or build from source on a machine that
has the repository (`AUTHSERVICE_VERSION=local docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build`).

## Deploy on an Ubuntu server with Docker

One server, everything in containers, HTTPS certificates obtained and renewed by the proxy. It is the right size for a first product and a
few hundred users; it is a single machine, so it is also a single point of failure (see "What this setup does not give you").

**You need:** an Ubuntu 22.04 or 24.04 server with a public IP address and `sudo`; two names that point at it (an `A` record for the API,
for example `auth.example.com`, and one for the console, for example `app.example.com`; with Cloudflare, set both to **DNS only**, the grey
cloud); and a Brevo account if you want real email (see "Production email" below; the first trial can use `EMAIL_PROVIDER=File`).

**1. Check DNS.** From your own computer, both names must answer with the server's address:

```bash
dig +short auth.example.com
dig +short app.example.com
```

**2. Prepare the server.** Log in over SSH and run:

```bash
sudo apt-get update && sudo apt-get -y upgrade
sudo apt-get install -y ca-certificates curl ufw unattended-upgrades

# Only SSH, HTTP and HTTPS may reach the server. HTTP is needed for the certificates.
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw --force enable

# Docker Engine and the compose plugin (Docker's own installer).
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker "$USER"
```

Log out and back in so the `docker` group applies, then check `docker compose version`. Docker opens the ports it publishes itself and
`ufw` does not restrict them, which is why this stack publishes only 80 and 443 and nothing else: the database, the API and the web console
are not reachable from outside at all.

**3. Get the five small files and log in to the registry.** No source code and no `git` are needed on the server. Open the repository's
**Releases** page, pick the version, and download the attached files: `docker-compose.yml`, `Caddyfile`, `env.example`, `backup.sh` and
`check.sh`. (`env.example` is the repository's `.env.example`; GitHub does not keep a leading dot on an attachment's name.) Put them in one folder on
the server, for example by copying them from your own computer:

```bash
# on your computer, in the folder you downloaded them to
scp docker-compose.yml Caddyfile env.example backup.sh check.sh ubuntu@your-server:~/authservice/
```

(Or on the server, if you have the GitHub command-line tool signed in: `gh release download v1.0.0 --repo ghazi1567/AuthService --dir ~/authservice`.)

The images are private, like the repository, so the server logs in to the registry once. In GitHub, **Settings, Developer settings, Personal access
tokens, Tokens (classic)**: create one with only the `read:packages` permission, then on the server:

```bash
cd ~/authservice
chmod +x backup.sh check.sh
echo "<the token>" | docker login ghcr.io -u <your GitHub user name> --password-stdin
```

(If you would rather not use a token, set the three packages to public under the repository's **Packages**: they contain the program, no secrets.)

**4. Fill in the settings.** Everything is in one file that never leaves the server:

```bash
cp env.example .env
chmod 600 .env
nano .env
```

Set `AUTHSERVICE_VERSION` to the release you downloaded the files from, without the `v` (`1.0.0`). Set `AUTH_HOST` and `APP_HOST` to your two names and
`ACME_EMAIL` to an address you read. Create the secrets on the server and paste them in:

```bash
openssl rand -base64 24   # POSTGRES_PASSWORD
openssl rand -base64 32   # AUTH_KEY_ENCRYPTION_KEY
```

**Copy `AUTH_KEY_ENCRYPTION_KEY` to a password manager or another safe place now**, not on this server and not with the database backups: without it
the stored signing keys cannot be read. Leave `BREVO_API_KEY` and the sender empty and set `EMAIL_PROVIDER=File` for a first trial (emails are
written to a folder inside the API container instead of sent); switch to Brevo once the sender domain is ready.

**5. Start it.** In the folder with the files:

```bash
docker compose pull
docker compose up -d
docker compose ps
```

The download takes a minute or two. Start order is automatic: the database, the migration job, the API, the web console, and then the proxy,
which asks Let's Encrypt for a certificate for each name. When `docker compose ps` shows `api`, `web` and `db` as `healthy` and `caddy` as
`Up`, run the check script (from the server or from your own computer; it makes the same requests a visitor and a product would):

```bash
./check.sh auth.example.com app.example.com
```

Every line should say `ok`: the API is ready (database reachable, migrated, a signing key), the public keys are published, `/metrics` is refused,
the console and its sign-in page are served, HTTP is sent to HTTPS, and HTTPS is enforced. Then open `https://app.example.com` in a browser. If a
certificate is not issued, see "If something does not work" below.

**6. Make the first platform admin.** Nobody can become a platform admin through the website; it is a database flag, by design. Sign up at
`https://app.example.com/signup` and verify your email (with `EMAIL_PROVIDER=File` the link is in the API container: `docker compose exec api
sh -c 'cat /tmp/emails/*'`), then:

```bash
docker compose exec db psql -U auth -d auth -c "UPDATE users SET is_platform_admin = true WHERE email = 'you@example.com';"
```

Sign out and in again; the platform console is at `/platform`. Register your product there (README, "Platform admins" and "Applications").

**7. Backups.** `backup.sh` writes a dump of the database to a `backups` folder next to it and removes dumps older than 14 days. Run it every night
from cron, and copy the dumps off the server (a backup on the machine that fails is not a backup):

```bash
crontab -e
# add this line (use your own path):
0 3 * * * /home/ubuntu/authservice/backup.sh >> /home/ubuntu/authservice-backup.log 2>&1
```

Run it once by hand now, and **try a restore before you need one**, into a scratch database so nothing live is touched:

```bash
./backup.sh
docker compose exec -T db createdb -U auth restoretest
docker compose exec -T db pg_restore -U auth -d restoretest --no-owner < backups/auth-<the file just written>.dump
docker compose exec -T db psql -U auth -d restoretest -c "select count(*) from users;"
docker compose exec -T db dropdb -U auth restoretest
```

To restore for real (the data is lost or damaged): `docker compose stop web api`, then `docker compose exec -T db dropdb -U auth auth`, `createdb -U auth
auth`, `pg_restore -U auth -d auth --no-owner < <file>` the same way, then `docker compose start api web`. The dump does not contain
`AUTH_KEY_ENCRYPTION_KEY`: keep `.env` safe separately, or the restored signing keys cannot be read.

**8. Updating.** Read the new release's notes, take a backup, change the version and pull:

```bash
./backup.sh
nano .env                      # AUTHSERVICE_VERSION=1.1.0
docker compose pull
docker compose up -d
./check.sh auth.example.com app.example.com
```

If the release came with newer `docker-compose.yml` or `Caddyfile` attachments (the release notes say so), download them over the old ones first.
The migration job runs first and the API waits for it; the console and API restart in a few seconds. People signed in to the console are signed out
by the restart (see below).

**Going back** is the same with the previous version in `.env`: `AUTHSERVICE_VERSION=1.0.0`, `docker compose pull`, `docker compose up -d`. A
migration is not undone by that, which is why a migration must stay compatible with the previous version (add before you remove). If a release
broke the data, restore the backup you took before updating (step 7).

**Day to day:** `docker compose logs -f api` (or `web`, `caddy`) shows what is happening; logs rotate by themselves (5 files of 10 MB per container).
`docker compose restart web` restarts one part. The containers start again by themselves after a reboot.

### What the proxy and the stack do for you

- **HTTPS** for both names, renewed automatically, with HTTP redirected to HTTPS and a Strict-Transport-Security header. Keep the `caddy_data`
  volume: it holds the certificates.
- **The real visitor address.** The proxy tells the apps who is calling; the web console passes it on to the API when it calls for a visitor. Rate
  limits and the audit log therefore see each visitor, not the proxy. Both apps believe those headers only from the stack's own private network
  (`DOCKER_SUBNET`, 172.29.200.0/24 unless you change it) and never from the internet.
- **`/metrics` is not served to the internet.** The proxy answers 404 for it. A scraper inside the stack's network can still read it (see
  "Health checks and metrics").
- **Cookie keys are kept** in the `webkeys` volume, so a restart does not make every cookie unreadable.

### If something does not work

| What you see | Look at |
|---|---|
| The browser says the connection is not private, or `docker compose logs caddy` shows the certificate was refused | DNS must point at this server (step 1), ports 80 and 443 must be open (step 2), and with Cloudflare the records must be **DNS only**. Fix it and `docker compose restart caddy`; Let's Encrypt limits repeated failures, so do not retry in a loop. |
| `docker compose ps` shows `migrate` exited with an error | `docker compose logs migrate`: usually the database password in `.env` does not match the existing database volume. |
| `api` stays unhealthy | `docker compose logs api`: a missing `AUTH_KEY_ENCRYPTION_KEY` or a database that is not reachable. |
| Sign-in answers "temporarily unavailable" | The rate-limit table is missing, so the migration did not run: `docker compose run --rm migrate`. |
| The web console shows an error page about the API | `docker compose logs web api`; check `curl https://auth.example.com/health/ready`. |
| Everyone limited at once after a few sign-ins | Both apps must trust the stack's network: `DOCKER_SUBNET` in `.env` must be the network the stack really uses (`docker network inspect authservice_default`). |

### What this setup does not give you

- **A second server.** If this machine goes down, so does everything. Restoring from a backup onto a new server (steps 2 to 5 with the restore in
  step 7) is the recovery.
- **Sessions that survive a restart.** The console keeps who is signed in in the web container's memory. Restarting or updating the console signs
  everyone out; they sign in again. (Running two copies of the console behind a load balancer would not work for the same reason.)
- **Monitoring.** Nothing watches the server. At least add an uptime check on `https://auth.example.com/health/ready` with an alert to you, and see
  "Health checks and metrics" for what to alert on.
- **Cloudflare's orange cloud.** Keep the records DNS only. Turning the proxy on puts Cloudflare's addresses between visitors and this server, and
  the stack would then see Cloudflare, not the visitor, until the proxy is told to trust Cloudflare's ranges. Ask before enabling it.

## What you need before you start

1. **PostgreSQL 17** with an empty database and a user that owns it.
2. **`Auth__Tokens__KeyEncryptionKey`**: 32 random bytes, base64 (`openssl rand -base64 32`). It encrypts the signing keys stored in the
   database. Set it **before the first start**, give **every instance the same value**, and keep a backup: without it the stored signing
   keys cannot be read. Changing it later stops the service starting; the recovery is to empty the `signing_keys` table, after which a
   new key is created and everyone has to log in again.
3. **A way to send email.** Brevo: an API key and a **verified sender** (ideally on a domain you own, authenticated with SPF and DKIM, or
   mail lands in spam).
4. **The address of the web console**, for the links in emails (`Email__PublicBaseUrl`). The service builds
   `/verify-email?token=...`, `/reset-password?token=...` and `/accept-invitation?token=...` links; the web console's pages receive them.
   The example compose file sets it from `APP_HOST`.
5. **HTTPS in front of the API.** The container speaks plain HTTP on 8080. Put a reverse proxy or the platform's load balancer in front
   of it to terminate TLS. Tokens and passwords must never cross the network unencrypted.

## Settings

All settings are environment variables. A double underscore stands for the colon in the names used in the README.

| Variable | Required | Meaning |
|---|---|---|
| `ConnectionStrings__AuthDb` | yes | `Host=...;Port=5432;Database=auth;Username=...;Password=...`. Adding `;GSS Encryption Mode=Disable` silences a harmless missing-library message on slim images. |
| `Auth__Tokens__KeyEncryptionKey` | yes | See above. The service refuses to start without it. |
| `Email__Provider` | yes | `Brevo` (`File` only writes emails to disk, for a trial). The service refuses to start without it. |
| `Email__BrevoApiKey` | with Brevo | A secret. |
| `Email__FromAddress`, `Email__FromName` | with Brevo | The sender; the address must be verified in Brevo. |
| `Email__PublicBaseUrl` | yes | Where links in emails point, for example `https://app.example.com`. |
| `Auth__Tokens__Issuer` | no | `iss` of every token. Default `AuthService`. Products that are not set up from the discovery document must be configured with the same value. |
| `Auth__Discovery__PublicBaseUrl` | behind a proxy | The API's own public address, such as `https://auth.example.com`. It is what `/.well-known/auth-service-configuration` tells products, and what the console shows beside a new client secret. The compose file sets it from `AUTH_HOST`. |
| `Auth__Proxy__TrustedProxies__0` (`__1`...) / `Auth__Proxy__TrustedNetworks__0` | behind a proxy | The proxy's address or CIDR range. See "Behind a proxy". Blank values are ignored. |
| `Auth__RateLimit__Store` | no | Where rate-limit counters are kept: `Postgres` (the default) or `Memory`. See "Rate limits with several instances". |
| `Observability__AllowedNetworks__0` (`__1`...) | to scrape `/metrics` | A CIDR range allowed to read `/metrics`, for example the monitoring server's `10.0.0.0/8`. Loopback is always allowed. See "Health checks and metrics". A typo stops the service from starting. |
| `Auth__Webhooks__AllowPrivateAddresses` | never | Leave unset in production: it would let a product point webhooks inside your network. |

The rate limits, token lifetimes and lockout rules have safe defaults; the README's Configuration table lists them.

## First deployment

1. **Create the database** and a user for it.
2. **Run the migrate job** against it, with the connection string as its only argument:
   ```
   docker run --rm authservice-migrate --connection "Host=...;Port=5432;Database=auth;Username=...;Password=..."
   ```
   On a platform, make it a one-off job or a release command that must succeed before the API starts. It is safe to run again: it
   applies only what is missing, and takes a database lock, so two runs at once take turns.
3. **Start the API** with the settings above. On first start it creates the first signing key and the Auth Service's own built-in
   product.
4. **Check it:**
   - `GET /health/ready` answers `200` (see "Health checks and metrics"; use it as the health check).
   - `GET /.well-known/jwks.json` returns one key.
   - Sign up through your front end (or `POST /api/auth/signup`) and confirm that the verification email arrives.
5. **Make the first platform admin.** Nobody can become a platform admin through the API; it is a database flag, by design. Sign up and
   verify yourself first, then:
   ```sql
   UPDATE users SET is_platform_admin = true WHERE email = 'you@example.com';
   ```
   The flag is read from the database on every request, so it applies at once (reload `/platform`; signing out and in is not needed).
   From there you register products through `/platform/products` or `/api/auth/admin/applications` (README, "Platform admins").

## Behind a proxy

Behind a load balancer every connection comes from the balancer, so every client would share one per-IP rate limit and every audit
entry would name the balancer. List the proxy so the service believes its `X-Forwarded-For`:

- `Auth__Proxy__TrustedProxies__0=<the proxy's address>`, or `Auth__Proxy__TrustedNetworks__0=<a CIDR range>` when its address changes.
- Only a connection from a listed proxy is believed; from anyone else the header is ignored, so a caller cannot pick their own address.
- `Auth__Proxy__ForwardLimit` (default 1) is the number of proxies in a chain.
- **Find the address the API sees.** With Docker on one machine it is the Docker network gateway, not `127.0.0.1`. Sign up once, then
  `SELECT source_ip FROM audit_entries ORDER BY occurred_at DESC LIMIT 1;`: that is the proxy. Use it, and check that a later entry
  shows a real client address.
- Too wide a range lets any host in it choose its own address. List as little as you can.

## Production email

Verification, password-reset and invitation emails are how people get into the product, so they must reach an inbox. A personal mailbox
as the sender does not: messages tend to land in spam and fail DMARC alignment. Set this up once, before the first real user.

**1. Authenticate a domain you own in Brevo.** In Brevo, *Senders, Domains & Dedicated IPs → Domains → Add a domain* (use a subdomain
such as `mail.example.com` if you do not want to touch the main domain's records). Brevo shows the exact DNS records to create; add them at
your DNS host and press *Authenticate* until the domain shows as authenticated:

| Record | What it is for |
|---|---|
| Brevo's verification `TXT` (`brevo-code:...`) | Proves you own the domain. |
| **SPF**: a `TXT` on the domain that includes Brevo's servers (`include:spf.brevo.com`) | Says Brevo may send for the domain. If the domain already has an SPF record, **add the include to it**: a domain may have only one. |
| **DKIM**: the record(s) Brevo shows, usually `CNAME`s under `_domainkey` | Lets receivers check each message was signed by Brevo for your domain. |
| **DMARC**: a `TXT` on `_dmarc.<domain>`, for example `v=DMARC1; p=none; rua=mailto:dmarc@example.com` | Tells receivers what to do when SPF or DKIM fail, and where to send reports. Start with `p=none` and read the reports for a couple of weeks; move to `quarantine` once real mail passes. |

**2. Send from that domain.** Add a sender on the authenticated domain in Brevo, then set `Email__FromAddress` to it (for example
`no-reply@mail.example.com`) and `Email__FromName` to the product's name. The address must be on the authenticated domain: that is what makes
the DMARC check line up. Never send from a personal mailbox in production.

**3. Point the links at the real front end.** `Email__PublicBaseUrl` is the public address of the app that serves `/verify-email`,
`/reset-password` and `/accept-invitation` (for example `https://app.example.com`), with no trailing path. Open a link from a real email to
check that its flow completes.

**4. Check it before launch**, with the list in [the release checklist](release-checklist.md#production-email): a message to a Gmail and an
Outlook mailbox lands in the inbox and passes SPF, DKIM and DMARC in its headers.

**The API key is a platform secret.** Keep `Email__BrevoApiKey` in your host's secret store (not in the repository, not in a committed
`.env`, not in a chat). It is never logged by the service. To rotate it: create a new API key in Brevo (*SMTP & API → API Keys*), set the
new value in the secret store and redeploy or restart the API, check that the next verification email arrives, **then** delete the old key
in Brevo. Do it at once if the key may have leaked.

**Bounces and complaints.** The service hands a message to Brevo and is done: a later bounce is *not* reported back, so an address that
bounces looks "sent" in `email_outbox`. Someone has to look at Brevo:

- Check *Transactional → Logs* (and *Contacts → Blocked contacts*) at least weekly and after every launch or invitation campaign.
- A **hard bounce** (no such mailbox, no such domain) blocks the address in Brevo. It is nearly always a typo in an invitation: ask the
  inviter to check the address and invite again; do not try to send to it again.
- A **soft bounce** (full mailbox, temporary failure) is retried by Brevo; act only if it repeats.
- A **spam complaint** blocks the address. Do not unblock it. A rising complaint rate (above roughly 0.1%) or hard-bounce rate (above
  roughly 2%) puts the sender's reputation, and so every user's emails, at risk: stop invitation sending until you know why.
- Optionally add a Brevo webhook for `hardBounce` and `spam` events to a channel your team reads, so you do not rely on remembering.

An email the service gave up on after its last retry shows as `Failed` in `email_outbox` and in the `auth_email_outbox_messages{status="failed"}`
metric (see "Health checks and metrics"): that means Brevo itself refused or could not be reached, a different problem from a bounce.

## Rate limits with several instances

The limits on login, password reset, verification resend, service tokens and token exchange are counted in the service's own database
(table `rate_limit_counters`), so every instance shares them and a restart does not clear them (decision 0008).

- **`Auth__RateLimit__Store`**: `Postgres` (the default) or `Memory`. `Memory` counts inside each process: right for development, tests and
  one instance, but with several instances a limit becomes the setting times the number of instances. Anything else stops the service
  from starting.
- **Requirements:** nothing beyond the database you already run. The migrate job creates the table, and it must run before the new version
  starts: until it has, the limited endpoints answer `503`. The database user needs no extra rights.
- **If the counters cannot be read, nothing fails open.** The request is refused with `503` `rate_limit_unavailable` and
  `Retry-After: 5`, and does nothing else. These endpoints need the same database, so this costs nothing that a database outage had not
  already cost, and it means an attacker cannot lift a limit by making the counters fail.
- **Size:** one small row per caller per limit, with the caller stored only as a hash. A worker deletes rows a minute after their window
  ends (`Auth__RateLimit__CleanupSeconds`, default 60), so the table is as large as the number of callers in the last few minutes.
- **Behind a proxy**, list the proxy in `Auth__Proxy__*` so the limits see the client's address and not the proxy's; otherwise everyone
  shares one counter.

## Health checks and metrics

None of these needs a login, because a platform's probe cannot send one.

| Endpoint | Use it as | Answers |
|---|---|---|
| `GET /health/live` | the liveness probe | `200` `{"status":"alive"}` while the process runs. It touches nothing else, so a database outage never gets the service restarted. |
| `GET /health/ready` | the readiness probe, and the load balancer's health check | `200` when the database is reachable with every migration applied **and** a signing key can sign right now; otherwise `503`. The body names the check that failed (`database`, `signingKey`) and nothing more: the reason is in the log (`Not ready: ...`). |
| `GET /metrics` | the Prometheus scrape target | Prometheus text. Only from loopback and the networks in `Observability__AllowedNetworks__0`; anyone else gets a plain `404`. |

`/health/ready` is also how you notice a release that went out **before its migration**: it stays `503` (`database: failed`) until the
migrate job has run, so a rolling deployment does not send traffic to the new version early.

`/metrics` is read from the connection's own address. Behind a proxy that is listed in `Auth__Proxy__*`, the address is the client's, so
point the scraper at the service directly (not through the proxy), and list the scraper's network. With Docker's published port the
scraper arrives from the bridge gateway (often `172.17.0.1`), so list that range too. What it holds:

- `auth_http_requests_total{method,route,status}` and `auth_http_request_duration_seconds` (a histogram): by route **template**, so
  `/api/auth/tenants/{tenantId:guid}` and never a path with an id in it. Paths that match no route are all `unmatched`.
- `auth_login_failures_total{reason}`: `wrong_password`, `unknown_account`, `account_locked`, `account_disabled`, `email_not_verified`.
- `auth_email_outbox_messages{status}` and `auth_webhook_outbox_messages{status}` (`pending`, `failed`), with
  `..._oldest_pending_age_seconds`: a growing number means mail or webhooks are not getting out.
- `auth_signing_key_age_seconds` (how long the key that signs now has been signing), `auth_signing_keys_published`,
  `auth_process_start_time_seconds`.

No label ever holds an email address, a name, an id or a token. Counters start at zero when the process starts; the monitoring system
turns them into rates. Suggested alerts: readiness failing for more than a minute; `auth_email_outbox_messages{status="failed"}` or
`auth_webhook_outbox_messages{status="failed"}` above zero; `auth_email_outbox_oldest_pending_age_seconds` above a few minutes; a burst
of `auth_login_failures_total`, or `5xx` in `auth_http_requests_total`.

## Publishing a release

A release is a Git tag, and CI does the rest. Nothing is built or pushed from a developer machine.

1. Merge what should be in the release to `main` and wait for its CI run to be green.
2. Tag the merged commit and push the tag (from any clone, or create the tag in GitHub's **Releases** page):

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

   Use semantic versions: `v1.0.0`, `v1.1.0`, `v1.1.1`; a pre-release such as `v1.1.0-rc.1` is published too but never becomes `latest`.
3. The **Publish images** workflow (`.github/workflows/publish-images.yml`) then runs, and each step needs the one before: it checks the tag is a
   semantic version on `main`; builds and runs the whole test suite; builds the API, web console and migration images for Intel and ARM and pushes
   them as `ghcr.io/ghazi1567/authservice-api`, `-web` and `-migrate` tagged with the version (and `latest` for a final release); starts the pushed
   images with the release's own compose file and runs `check.sh` against them; and only then creates the GitHub release, with the five files a
   server needs attached. A failure at any step creates no release. Images already pushed stay in the registry: fix the problem and tag the next
   patch version rather than reusing the tag.
4. The first time, the registry packages are private to the repository, like the repository itself. A server logs in with a token that has only
   `read:packages` (step 3 of "Deploy on an Ubuntu server with Docker").
5. Servers update when you say so (step 8 of the same guide). Work through [the release checklist](release-checklist.md) for the parts that need the
   real network.

Updating a running system: a migration is applied while the old version is still serving, so a migration must stay compatible with the
running version (add before you remove). Several instances can run together: key rotation, email and webhook workers all take database
locks so instances take turns. Rate-limit counters are shared through the database (see "Rate limits with several instances"), so a limit is
the same with one instance or ten. A migration is not undone by going back to an older version.

## Operations

- **Backups:** the database holds everything, including the encrypted signing keys. Back it up on a schedule and test a restore. Back up
  `Auth__Tokens__KeyEncryptionKey` separately, somewhere that is not the same place as the database backup.
- **Logs:** to standard output. Set `Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning` to drop the line per database command.
  Webhook logs name only ids and outcomes, never a callback URL, because a URL can hold a secret.
- **Signing keys** rotate themselves every 90 days with a day of overlap; `POST /api/auth/admin/signing-keys/rotate` replaces the key
  at once if one leaks, which invalidates every token it signed.
- **Products** check tokens locally against `https://<your host>/.well-known/jwks.json` with the `AuthService.Authorization` package
  (`src/AuthService.Authorization`), configured with this service's issuer and the product's own audience.

## Not covered here

Choosing a host, DNS and certificates, and the front end that serves the email links. Pick a host and these steps map directly onto it.
