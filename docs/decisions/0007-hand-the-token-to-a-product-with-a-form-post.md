# 0007. Hand a product its token with a form post

- Status: Accepted
- Date: 2026-11-03
- Tickets: AUTH-253 (UI05_T4), AUTH-251 (UI05_T2), AUTH-258 (APP-08), AUTH-259 (APP-09). Builds on [0006](0006-signed-in-session-kept-on-the-server.md).

## Context

When someone chooses a product in the picker, the web app trades their sign-in for a product token (the token exchange) and has to
give it to the product, which lives at its own address. The token must never be in a query string, a log or a referrer: an address
is kept in history, proxies and server logs, and sent on to other sites.

## Decision

- The web app opens a product with **`POST /choose-product/open`** (an anti-forgery checked form: organisation and product). It
  asks the API for the person's own products (APP-09) and takes the **launch address from the API's answer**, never from the
  request, so the person can't be sent anywhere the platform admin didn't set (APP-08). The product must be `Available` to this
  person, or nothing is exchanged.
- It then makes a **fresh token exchange** for that organisation and product (nothing is cached for products; a console token is a
  different token) and answers with a small page holding a **form that posts to the launch address**, with the token in the
  **request body** (`access_token`, `token_type`, `expires_in`), submitted by script at once and with a Continue button when scripts
  are off.
- That page is sent with `Referrer-Policy: no-referrer` (every response has it), `Cache-Control: no-store`, and a
  `Content-Security-Policy` whose `form-action` allows this site **and exactly the launch address's origin**, for that one response.
- The token is never logged. The page is built from a few fixed pieces with every value HTML-encoded.
- **How a product receives it:** the launch address must accept a `POST` of `application/x-www-form-urlencoded` with those three
  fields, check the token (its signature against the platform's published keys, its audience, its expiry), start its own session
  and answer with a redirect to its own page (303), so the token is not left in the browser's history or the back button.

## Alternatives considered

- **Token in the query string or a redirect.** Rejected: it is kept in history and logs and sent as a referrer.
- **A URL fragment.** It doesn't reach servers or referrers, but any script on the product's landing page and the browser history
  can read it, and a product with no script can't receive it at all.
- **A short-lived single-use code the product redeems with its own credentials.** Safer still (the token never passes through the
  browser) but needs new API endpoints and every product to implement the redemption. A reasonable next step if a product can't
  accept the form post; this decision doesn't prevent it.

## Consequences

- A product needs one endpoint that accepts the post. A product that wants more can switch to the code exchange later.
- The token does pass through the person's browser, as a form field for an instant. It is a 15-minute token for that one product
  and organisation, which the browser already holds for any product the person uses.
- A launch address that isn't https (except localhost, for development) is refused by the API, so the token isn't posted in the clear.
