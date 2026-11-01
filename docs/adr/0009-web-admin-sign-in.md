# ADR-0009: Owner sign-in to the web admin, with Google

- **Status:** **Proposed 2026-10-31** (ST-099). Built and tested on the Mac suite. **The owner chose
  Google** as the way owners sign in: not email and password, and not Microsoft. Nothing is switched on
  until the operator creates a Google OAuth client id and sets it (below, "Owner steps"). The owner
  decides the defaults listed under "Decisions for the owner" before this leaves Proposed.
- **Date:** 2026-10-31
- **Ticket:** ST-099 (owner signup and SSO, owed by ST-010; the policy model is ST-047's)
- **Deciders:** project owner (Google); build agent (everything else, for the owner to confirm)

## Context

ST-010 built the device side of tenancy: invites, activation, seats, offboarding, all driven by the
operator's command line. ST-099 asks for the web admin: an owner signs up, invites a technician, sets a
policy and sees the device activate. Four facts shape how an owner proves who they are:

1. **Device tokens already exist** (ST-008): HS256, one-hour access tokens with the device in `sub` and
   the tenant, user and device as claims, checked against the database on every request
   (`CallerPolicy`). An owner is not a device and must never be mistaken for one, in either direction.
2. **There is no mail provider** (ST-007), so a password-reset or magic-link flow has nothing to send
   with, and storing passwords is a liability this service does not have today.
3. **The owner chose Google.** Google Identity Services (GIS) in the browser hands the page a signed ID
   token. Checking it needs Google's public keys and the client id, and **no client secret**.
4. **The repository is public** and the laptop runner builds it. Nothing secret can be a default.

## Decision

### The flow

```
Browser (GIS button) --ID token--> POST /v1/admin/session
  backend: verify the token (below)
    owner row for this Google sub?  yes -> session cookie, {status: signed_in, csrf}
                                    no  -> address on Admin:SignupAllowlist?
                                             yes -> {status: needs_tenant}  (first-run screen asks the name)
                                             no  -> 403 signup_not_allowed
Browser --ID token + organisation name--> POST /v1/admin/signup
  backend: verify again, allowlist again, create tenant (Admin:NewTenantSeats) and owner -> session cookie
```

The page keeps Google's ID token in memory only between the two calls (an hour at most, Google's
lifetime); a sign-in that has expired by the time the name is entered is sent back to the start.

### Checking a Google ID token (`GoogleIdTokenVerifier`)

Every check is ours and none is the token's: the signature with **RS256 only**, against the key the
token names in **Google's published key set** (no trying every key); the issuer
**`accounts.google.com` or `https://accounts.google.com`**; the audience **exactly the configured client
id**; an **expiry that has not passed, with no grace**; **`email_verified` true** (boolean, or the older
string `"true"`); a subject and an address of sane length. Any failure is the same 401 with no reason.

Google's keys come from `https://www.googleapis.com/oauth2/v3/certs` (`GoogleSigningKeys`), the one
address Google documents, through a named client with a 10-second deadline and a 64 KB cap. The copy is
**cached for Google's max-age, held between 5 minutes and 24 hours**. A token naming an unknown key id
asks for a refresh, **at most once every 5 minutes counted from the last attempt, failed or not**, so a
stream of made-up key ids is not a stream of requests to Google. A fetch that fails keeps a copy that is
still fresh; with none it **fails closed** (503, "could not reach Google") and does not ask again for 30
seconds. A caller with a fresh copy to hand is never queued behind somebody else's fetch. Tests replace
the key source with a fake (`FakeGoogleKeys`, `NoGoogle`); no test calls Google.

### The admin session: an HttpOnly cookie, not a bearer token

A ScreenTail JWT (HS256) signed with a key derived from `Jwt:SigningKey` for admin sessions alone (below),
carried in a cookie named
`__Host-screentail-admin`: **HttpOnly, Secure, SameSite=Strict, Path=/**, no Domain. Its claims are the
tenant, the owner, `role=owner`, a CSRF value and the owner's session version, and its audience is
**`screentail-admin`**. It lasts `Admin:SessionLifetime` (8 hours by default) with no refresh; the
owner signs in with Google again.

**Why a cookie.** The admin page is the most privileged surface ScreenTail has: it issues the codes that
enrol machines on technicians' desks. A bearer token in page memory or storage is one script injection
away from being taken; an HttpOnly cookie is not readable by script at all. The existing device scheme
stays bearer, because devices are not browsers. The price of a cookie is CSRF, paid twice:

- **SameSite=Strict**: the browser does not send the cookie on any request another site starts.
- **Double-submit**: every request that is not a GET or HEAD must carry `X-ScreenTail-CSRF` equal to the
  CSRF value inside the signed session (compared in constant time). The page learns it from the sign-in
  answer or `GET /v1/admin/session` and keeps it in memory. A custom header also forces a CORS preflight
  that this API never approves. The session stays stateless: the value is in the token, not a table.

**Sign-out ends sessions on the server.** `POST /v1/admin/session/end` deletes the cookie and, when it
comes from the session's own page (it carries the CSRF value), moves the owner's `SessionVersion` on.
Every admin session carries the version it was issued with and `OwnerCheck` refuses one that no longer
matches, so a copied cookie stops working at once, in every browser, not at its expiry. Without the CSRF
value only the asking browser's cookie is deleted, so another site cannot sign the owner out everywhere.

### Devices and owners never cross

Told apart five ways, any one of which suffices: **the key** (admin sessions are signed with their own
key, HMAC-SHA256 of the fixed label `screentail-admin-session-key-v1` under `Jwt:SigningKey`, so one
secret is configured and neither scheme can check the other's signature; changing the label ends every
admin session); **the audience** (`screentail-api` for devices, `screentail-admin` for owners, and
startup refuses a `Jwt:Audience` equal to the admin one in any case or with a trailing slash); **the
transport** (the admin scheme reads only its cookie and never the Authorization header; the device
scheme reads only the header); **the claims** (`OwnerCheck` wants `role=owner` and an owner id and
refuses a token with a device id; `CallerCheck` wants a device id); and **the route policies** (admin
routes are behind `ActiveOwner`, authenticated by the admin scheme alone; device routes are behind
`ActiveCaller`, by the default scheme alone, and `AdminAccessTests` checks no route has both). Every
admin request reloads the owner and tenant and refuses either one disabled, like a revoked device.

### Small guards on every admin answer

Every response under `/v1/admin`, refusals included, carries `Cache-Control: no-store`: the CSRF value,
an invite's one-time code and the technician list are not kept by a browser or a proxy. The three
anonymous routes (sign-in, signup, sign-out; `AdminAccessTests` fails if a fourth appears) read at most
16 KB of body, and a body that says it is larger is refused with 413 before it is read. A key fetch that
fails is logged as a warning naming only the kind of failure.

### Tenant scoping

Every admin query is filtered by the tenant in the session and by nothing in the request; an id in a
path is looked up inside that tenant, and another tenant's id is the same 404 as an unknown one. Tests
seed a second tenant beside every read and write and prove none of it is returned or changed.

## Decisions for the owner (fail-closed defaults, built and switchable)

1. **Who may sign up: an operator-set allowlist, empty by default** (`Admin:SignupAllowlist`). Empty
   means nobody can create a tenant. An owner who already exists signs in whatever the list says.
2. **A new tenant's seats: `Admin:NewTenantSeats`, default 5** (the operator CLI's default too).
3. **One owner per tenant, one tenant per Google account** (unique indexes). No co-owners, no transfer
   of ownership, no recovery if the Google account is lost (the operator can re-point the owner row).
4. **Invite codes are shown once to the owner to hand on**, as the CLI prints them: there is no mail
   provider yet. The code is never shown again and only its hash is kept.
5. **Session lifetime 8 hours, no refresh; sign-out ends every session of that owner.**
6. **Owners see integration status but never a credential, not even its last four**, and do not manage
   credentials from the web; technicians still do from Settings (ST-009's rule that any device of the
   tenant may manage them is unchanged here).
7. **Google's keys out of reach means no sign-in** rather than checking against a stale copy.
8. **Signup matches the Google account by its subject**; the address is checked against the allowlist
   only when a tenant is created. Only ASCII addresses can sign up, compared without regard to ASCII
   case: an address with any other character is refused rather than case-folded (the Kelvin sign
   lowers to a plain k).
9. **No `hd` (Google Workspace domain) check.** Any Google account whose verified address is on the
   allowlist can sign up, a consumer Gmail account included. Requiring a Workspace domain is a setting
   the owner may want.
10. **An allowlist entry is not consumed.** An allowlisted address can create one tenant per Google
    account it controls (the address is per account, the uniqueness is per subject); a second account
    showing the same verified address could create a second tenant.
11. **Locking local-only off is allowed**, as the operator's CLI allows it: `localOnly` false with
    `localOnlyLocked` true stops technicians turning local-only on.

## Owner steps

1. In Google Cloud Console, create an OAuth client id of type **Web application**, with the admin's
   origins under **Authorized JavaScript origins** (for local work `http://localhost:5173`,
   `http://localhost:4173` and `http://localhost`). No redirect URI and no client secret are used.
   `docs/dev/web-admin.md` has the steps.
2. Set `Admin__GoogleClientId` on the backend and `VITE_GOOGLE_CLIENT_ID` for the web build to that id,
   and `Admin__SignupAllowlist__0` (and `__1`, ...) to the addresses that may create a tenant.
3. Serve the web app and the API from **one origin** (a reverse proxy routing `/v1` to the API), as
   Vite's proxy does in development. The cookie is host-only and SameSite=Strict on purpose.

## Alternatives considered

- **Bearer token in page memory, refreshed by a cookie.** Rejected for now: two token kinds for one
  page, and the refresh cookie needs the same CSRF care anyway.
- **ASP.NET Core's Google authentication handler (server-side code flow).** Needs a client secret in
  the deployment and a redirect URI per environment; GIS with ID-token verification needs neither.
- **Email and password, or Microsoft.** Not the owner's choice. Microsoft can be added beside Google
  later as a second verifier issuing the same session.

## Consequences and what is not done

- No rate limit on the two anonymous sign-in routes yet (each costs one signature check and, at most
  every five minutes, one key fetch). A deployment behind a proxy can limit them there.
- No GIS `nonce`: an ID token captured in transit could be replayed within its hour. HTTPS end to end is
  assumed; a nonce bound to a pre-sign-in cookie is the follow-up if that assumption weakens.
- No Content-Security-Policy header is set by this change; the page loads GIS from
  `accounts.google.com`, which a CSP would have to allow.
- Exclusions, redaction patterns and telemetry are not policy fields yet (ST-047), so the editor does
  not show them; the page says so.
- Local development needs a browser that keeps Secure cookies on `http://localhost` (Chrome, Edge and
  Firefox do).
