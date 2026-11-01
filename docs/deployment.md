# Deploying AuthService

The service ships as two container images built from the one `Dockerfile`. They run on anything that runs containers (a VPS with
Docker, Railway, Render, Fly, Azure Container Apps, Kubernetes): the host decides how, this page says what.

| Image | Build | What it does |
|---|---|---|
| API | `docker build -t authservice-api .` | The web service. Listens on port **8080**, runs as a non-root user. |
| Migrate | `docker build --target migrate -t authservice-migrate .` | A one-off job: applies every pending database migration, then exits. |

The API **never migrates on startup** ([decision 0003](decisions/0003-migrations-applied-explicitly.md)). Every release runs the migrate
job first, then starts the new API.

`deploy/docker-compose.yml` is a worked example for a single machine (PostgreSQL, the migrate job and the API). It was run for real
while writing this page.

## What you need before you start

1. **PostgreSQL 17** with an empty database and a user that owns it.
2. **`Auth__Tokens__KeyEncryptionKey`**: 32 random bytes, base64 (`openssl rand -base64 32`). It encrypts the signing keys stored in the
   database. Set it **before the first start**, give **every instance the same value**, and keep a backup: without it the stored signing
   keys cannot be read. Changing it later stops the service starting; the recovery is to empty the `signing_keys` table, after which a
   new key is created and everyone has to log in again.
3. **A way to send email.** Brevo: an API key and a **verified sender** (ideally on a domain you own, authenticated with SPF and DKIM, or
   mail lands in spam).
4. **The address of your front end**, for the links in emails (`Email__PublicBaseUrl`). The service only builds
   `/verify-email?token=...`, `/reset-password?token=...` and `/accept-invitation?token=...` links; the pages that receive them are the
   front end's job.
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
| `Auth__Tokens__Issuer` | no | `iss` of every token. Default `AuthService`. Products must be configured with the same value. |
| `Auth__Proxy__TrustedProxies__0` (`__1`...) / `Auth__Proxy__TrustedNetworks__0` | behind a proxy | The proxy's address or CIDR range. See "Behind a proxy". Blank values are ignored. |
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
   - `GET /` answers `200` with the text `AuthService` (use it as the health check).
   - `GET /.well-known/jwks.json` returns one key.
   - Sign up through your front end (or `POST /api/auth/signup`) and confirm that the verification email arrives.
5. **Make the first platform admin.** Nobody can become a platform admin through the API; it is a database flag, by design. Sign up and
   verify yourself first, then:
   ```sql
   UPDATE users SET is_platform_admin = true WHERE email = 'you@example.com';
   ```
   Log in again for a token that carries it. From there you register products through `/api/auth/admin/applications` (README,
   "Platform admins").

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

## Releasing a new version

1. Build both images from the same commit.
2. Run the migrate job. A migration is applied while the old version is still serving, so a migration must stay compatible with the
   running version (add before you remove).
3. Start the new API; stop the old one. Several instances can run together: key rotation, email and webhook workers all take database
   locks so instances take turns. Rate-limit counters are kept in memory per instance, so with several instances the effective limit is the setting times the number of instances.
4. Roll back by starting the old image. A migration is not undone by that, which is why step 2 matters.

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
