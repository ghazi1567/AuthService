# Release checklist

What the automated tests cannot reach, to be checked once against the environment a release goes to. The tests cover everything that can
run on a CI machine; this covers the parts that need the real network. Tick each line, and note who did it and when in the release
record.

## Webhook delivery to a real receiver (TST-04, AUTH-207)

CI delivers events to a real HTTPS server over real TLS, with real sockets, a real DNS lookup of `localhost`, redirects, timeouts,
retries, the 24-hour deadline and resend (`tests/AuthService.IntegrationTests/Tst04`). Its certificate comes from an authority that
exists only for the test run. These parts it cannot show, because they depend on the public internet and on your hosting network:

Do this after the migrate job has run and the new API is serving, with `Auth__Webhooks__AllowPrivateAddresses` **unset** (production).

1. **Pick a receiver.** A product's real callback in a staging organisation, or a throwaway public HTTPS endpoint you control that logs
   the headers and body it receives and can answer with a status you choose. It must have a certificate from a publicly trusted
   authority and a public DNS name.
2. **Register it.** With a product's service token: `PUT /api/auth/applications/me/webhook` with `{"url": "https://<receiver>/hooks"}`.
   Keep the signing secret it returns.
3. **Cause an event.** Subscribe a test organisation to the product (`POST /api/auth/tenants/{tenantId}/subscriptions` with the
   product's service token).
4. **Check what arrived** (within a few seconds):
   - [ ] The request came over HTTPS and the receiver's certificate was accepted (no `HttpRequestException` in the event's `lastError`
         at `GET /api/auth/admin/webhooks`).
   - [ ] `X-Auth-Event-Id`, `X-Auth-Event-Type`, `X-Auth-Timestamp` and `X-Auth-Signature` are present.
   - [ ] Recomputing `HMAC-SHA256(secret, "{timestamp}.{body}")` in lowercase hex gives the `v1=` value in the signature.
   - [ ] The event shows `Delivered` in the platform admin's webhook list.
5. **Failure handling.** Make the receiver answer `503`, cause another event, and check:
   - [ ] The event stays `Pending` with `lastError` `HTTP 503` and a later `nextAttemptAt` (about a minute).
   - [ ] After the receiver answers `200` again, the next retry delivers it.
6. **Redirect.** Make the receiver answer `302` to another address you can watch.
   - [ ] The event stays `Pending` with `lastError` `HTTP 302`, and the other address receives nothing.
7. **Private addresses are refused.** Point a DNS name you control at `127.0.0.1` (or a private address of your network), register
   it as the callback by editing the product's `webhook_url` in the database (registration itself refuses such names only when they are
   written as private addresses or `localhost`).
   - [ ] The event stays `Pending` with `lastError` `HttpRequestException`, and nothing reaches the address.
8. **Clean up.** Restore the product's real callback, and delete the test organisation's events or leave them to expire.

If a step fails, do not release: the delivery path is the only way a product learns that a tenant was subscribed or suspended.

## Production email (PLT-06, AUTH-203)

Nothing in CI can send real mail. Do this once for a new sending domain, and again after changing the domain, the sender or the Brevo
account. Setup is in [the deployment guide](deployment.md#production-email).

1. **Domain.** In Brevo the sending domain shows as authenticated (SPF, DKIM and DMARC records all green).
   - [ ] `Email__FromAddress` is on that domain, and the production settings use it.
2. **Send the three emails** to a Gmail and an Outlook test mailbox: sign up (verification), *Forgot password* (reset) and invite a new
   person (invitation).
   - [ ] Each lands in the **inbox**, not spam.
   - [ ] In the message headers (Gmail: *Show original*): `SPF: PASS`, `DKIM: PASS` and `DMARC: PASS`, with the From domain aligned.
3. **Links.** For each email, open the link from the message itself.
   - [ ] Verification link: the address becomes verified and sign-in works.
   - [ ] Reset link: a new password can be set.
   - [ ] Invitation link: the invited person can accept and sign in.
4. **Bounces.** Invite an address that does not exist on a real domain.
   - [ ] The bounce shows in Brevo's transactional logs, and whoever operates the service knows to look there.
5. **Secret.** [ ] `Email__BrevoApiKey` is in the secret store only, and the rotation steps in the deployment guide have been read by whoever
   would perform them.
