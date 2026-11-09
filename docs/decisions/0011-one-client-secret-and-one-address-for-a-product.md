# 0011. A product is set up from one address and one client secret

- Status: Accepted
- Date: 2026-11-09
- Tickets: AUTH-274 (ONB-03), part of the onboarding plan AUTH-272 to AUTH-276; related [0009](0009-publish-the-authorization-package-to-github-packages.md), [0010](0010-publish-the-authorization-package-publicly-on-nuget-org.md)

## Context

Connecting a product took many hand-copied values: the Auth Service's address, the issuer, the keys address, the product's audience,
its client id and its client secret. Each was a chance for a typo, and a team that got one wrong saw only that every token was refused.
The owner asked that the platform admin do everything in the portal and that the product need only the client secret.

## Decision

- **One string.** Registering a product and replacing its secret also return `credential`, the client id and the secret joined by a dot
  (`cl_<id>.<secret>`). The token endpoint accepts it as `client_secret` alone. The old pair still works, so nothing deployed breaks.
- **One address.** The Auth Service publishes `GET /.well-known/auth-service-configuration`, public: the issuer and the addresses of the API,
  the keys, the token endpoint and the console. A product that knows the Auth Service's address and its credential learns the rest. A
  product reads its own audience and permission keys from `GET /api/auth/applications/me` with its service token.
- **Two settings, not a connection string.** The product's team sets the Auth Service address and the credential: two named settings,
  easy to put in any host's configuration and to read in a log without leaking the secret (the address is not secret). A single string
  that embeds the address was considered and rejected for that reason.
- **The address is configured, not guessed, behind a proxy.** `Auth:Discovery:PublicBaseUrl` names the API's public address; without it the
  request's own address is used. The console reads the document by the API's internal name, so deployments set it (the compose file does).

## Alternatives considered

- OIDC discovery (`/.well-known/openid-configuration`): the document would imply an OIDC flow the Auth Service does not have, and clients
  would expect `/authorize`. A name of its own says what it is.
- Embedding the address in the credential: one value fewer, but a secret that changes whenever the address does, and a secret that carries a
  host name is harder to rotate and to handle safely.

## Consequences

- A credential is a full secret: it identifies the product and proves it. It is shown once and is handled exactly as the secret was.
- The discovery document is cached for five minutes by clients and is part of the public contract with the package (1.1.0, the next story).
- A deployment that does not set `Auth:Discovery:PublicBaseUrl` behind a proxy shows an internal address in the console's settings box until it does.
