# ADR-0009: Owner sign-in to the web admin, with Google

- **Status:** **Proposed 2026-10-31** (ST-099). Built and tested on the Mac suite. **The owner chose
  Google** as the way owners sign in: not email and password, and not Microsoft. Nothing is switched on
  until the operator creates a Google OAuth client id and sets it (below, "Owner steps"). The owner
  decides the defaults listed under "Decisions for the owner" before this leaves Proposed.
  **Hardened 2026-11-01:** the three items this record first listed as owed (a rate limit on the
  anonymous routes, a single-use sign-in nonce, and a Content-Security-Policy with related headers) are
  built and tested; "Hardening" below has the design.
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
Browser --> POST /v1/admin/session/nonce  -> {nonce, expiresAt, expiresInSeconds}   (one per button drawing)
Browser: google.accounts.id.initialize({client_id, nonce}); Google signs the nonce into the ID token
Browser (GIS button) --ID token--> POST /v1/admin/session
  backend: verify the token (below), and its nonce: issued here, unexpired, unused
    owner row for this Google sub?  yes -> session cookie, {status: signed_in, csrf}
                                    no  -> address on Admin:SignupAllowlist?
                                             yes -> {status: needs_tenant}  (first-run screen asks the name)
                                             no  -> 403 signup_not_allowed
Browser --ID token + organisation name--> POST /v1/admin/signup
  backend: verify again (nonce included), allowlist again, use the nonce up,
           create tenant (Admin:NewTenantSeats) and owner -> session cookie
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
an invite's one-time code and the technician list are not kept by a browser or a proxy. The four
anonymous routes (the sign-in nonce, sign-in, signup, sign-out; `AdminAccessTests` fails if a fifth
appears) read at most 16 KB of body, and a body that says it is larger is refused with 413 before it is
read. A key fetch that
fails is logged as a warning naming only the kind of failure.

### Tenant scoping

Every admin query is filtered by the tenant in the session and by nothing in the request; an id in a
path is looked up inside that tenant, and another tenant's id is the same 404 as an unknown one. Tests
seed a second tenant beside every read and write and prove none of it is returned or changed.

### Hardening (built 2026-11-01)

**1. A rate limit on the anonymous routes** (`AnonymousRateLimit`, ASP.NET Core's own rate limiter).
The sign-in nonce, sign-in, signup and a device's activation with an invite code share one fixed-window
count per client address: **10 calls a minute** by default (`RateLimit:PermitLimit`,
`RateLimit:Window`, checked at startup). The call past it is refused with **429, a `Retry-After` in
seconds and no body**, by middleware that runs before authentication and before the endpoint, so a
refused call never reaches Google's keys or the token verifier. An IPv6 caller is counted by its /64.
**Sign-out is not counted**: it acts only with the session and its CSRF value, and ending a stolen
session must never wait for a window. The device token exchange is not counted either (its secret is
256 bits and every device behind the MSP's one address renews hourly).

The client address is the connection's. **No forwarded header is read unless the operator lists the
proxy** (`ForwardedHeaders:KnownProxies:0`, `ForwardedHeaders:KnownNetworks:0` in CIDR form, both empty
by default); then `X-Forwarded-For` and `X-Forwarded-Proto` are honoured only on connections from those
peers, only the last entry (the one the listed proxy wrote, `ForwardedHeaders:ForwardLimit`, default 1)
counts, and ASP.NET Core's own default of trusting loopback is cleared. Without the list, behind a proxy
every caller shares the proxy's count, which is why listing it is an owner step for any hosted
deployment. The counts are in memory, per API instance.

**2. A single-use nonce in every Google ID token** (`SignInNonces`, table `admin_sign_in_nonces`). Before
drawing Google's button the page asks `POST /v1/admin/session/nonce` for 256 random bits and passes them
to `google.accounts.id.initialize({ nonce })`; Google signs the value into the token's `nonce` claim.
Sign-in and signup accept a token only when that claim names a nonce **issued here, not expired and not
used**; missing, unknown, expired and used are all the same 401 as a bad signature. The nonce is **used
up in one conditional update** (`WHERE id = hash AND used_at IS NULL AND expires_at > now`) at the
moment a session starts or a tenant is made, so of two requests with the same token exactly one wins.
Learning that an account may create a tenant (`needs_tenant`) does not use it up, so the first-run
screen sends the same token on; a refused organisation name does not either. Lifetime **10 minutes**
(`Admin:SignInNonceLifetime`, 1 to 60 minutes), and the answer carries `expiresInSeconds` so the page
draws a fresh button a minute before it runs out without trusting its own clock; it also draws one after
every token Google hands back. Only the nonce's SHA-256 is stored, and rows past their expiry are
deleted whenever a new one is issued.

*Why the database and not a cookie or memory.* Single use has to hold across a restart and across more
than one API instance. A signed cookie cannot be "used up" in the copy an attacker captured with the
token, and a store in memory forgets on restart and differs between instances. The table belongs to no
tenant, because a nonce is issued before anyone is known.

**3. Security headers.** Every API answer, refusals and errors included (added as headers are sent),
carries `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none';
form-action 'none'`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy:
no-referrer`, `Cross-Origin-Resource-Policy: same-origin` and a Permissions-Policy that turns off the
camera, microphone, location, payment and the rest (`SecurityHeaders`). The API serves JSON only, so
nothing in it needs any source.

The web page's headers are in `web/security-headers.ts`, the one place they are written: `vite preview`
sends them whole (so a local Lighthouse run sees the real policy), the dev server sends all but the CSP
(Vite's dev page runs inline scripts), and a deployment's web server must send them exactly
(`docs/dev/web-admin.md` has the lines). The backend does not serve the page. The CSP:

```
default-src 'none'; script-src 'self' https://accounts.google.com/gsi/client;
style-src 'self' 'unsafe-inline' https://accounts.google.com/gsi/style; img-src 'self'; font-src 'self';
connect-src 'self' https://accounts.google.com/gsi/; frame-src https://accounts.google.com/gsi/;
manifest-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'
```

This is Google's published list for Google Identity Services with **one addition, `'unsafe-inline'`
for styles**: checked in Chrome on 2026-11-01, the GIS client inserts a `<style>` element and sets
style attributes on its button, Chrome blocked all three under Google's list alone, and the button was
drawn unstyled (a page-sized Google logo). With the addition there were no violations on any page. A
style cannot run code, and the usual way to exfiltrate with one, a `url()` to another site, is still
blocked by `img-src` and `font-src`. Scripts stay `'self'` and the GIS client only, with no inline
script and no eval; a test builds the app and checks its page has no inline script, style or handler.
Beside the CSP: `nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` (the origin, never a path,
leaves the site; Google's button is told the origin), `Cross-Origin-Opener-Policy:
same-origin-allow-popups` (Google's COOP guidance for the pop-up), `X-Frame-Options: DENY`, and a
Permissions-Policy that leaves `identity-credentials-get` and `publickey-credentials-get` at the
browser's default, as Google's sign-in (FedCM, passkeys) may need them inside its own frame.

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
12. **Ten anonymous calls a minute per client address**, shared by the sign-in nonce, sign-in, signup
    and device activation (one sign-in spends two to four). Sign-out and the device token exchange are
    not counted.
13. **No proxy is trusted until one is listed**, so behind an unlisted proxy everyone shares one count.
14. **A sign-in nonce lasts 10 minutes**, from drawing the button to the session starting, the first-run
    screen included; past that the owner signs in with Google again.
15. **Inline styles are allowed in the page's CSP** because the GIS client needs them; scripts are not.
16. **Device activation is limited per address only.** An invite code is 10 characters from 31 (about
    49 bits), so guessing any live code is hopeless at 10 a minute per address; a cap on failed
    activations across all addresses (or longer codes) is the follow-up if many addresses guess at once.

## Owner steps

1. In Google Cloud Console, create an OAuth client id of type **Web application**, with the admin's
   origins under **Authorized JavaScript origins** (for local work `http://localhost:5173`,
   `http://localhost:4173` and `http://localhost`). No redirect URI and no client secret are used.
   `docs/dev/web-admin.md` has the steps.
2. Set `Admin__GoogleClientId` on the backend and `VITE_GOOGLE_CLIENT_ID` for the web build to that id,
   and `Admin__SignupAllowlist__0` (and `__1`, ...) to the addresses that may create a tenant.
3. Serve the web app and the API from **one origin** (a reverse proxy routing `/v1` to the API), as
   Vite's proxy does in development. The cookie is host-only and SameSite=Strict on purpose.
4. When that proxy exists (ST-007), **list it** in `ForwardedHeaders__KnownProxies__0` (or
   `ForwardedHeaders__KnownNetworks__0`, CIDR) so the rate limit counts each client rather than the
   proxy, and have the proxy **set** `X-Forwarded-For` (overwrite or append, never pass on the client's
   own). Have the web server send the headers in `web/security-headers.ts` (`appHeaders`) with the page.
5. Sign in once with a real Google account under the CSP and confirm the button draws, the pop-up
   answers and the session starts (the check this build could not make without a client id).

## Alternatives considered

- **Bearer token in page memory, refreshed by a cookie.** Rejected for now: two token kinds for one
  page, and the refresh cookie needs the same CSRF care anyway.
- **ASP.NET Core's Google authentication handler (server-side code flow).** Needs a client secret in
  the deployment and a redirect URI per environment; GIS with ID-token verification needs neither.
- **Email and password, or Microsoft.** Not the owner's choice. Microsoft can be added beside Google
  later as a second verifier issuing the same session.

## Consequences and what is not done

- The rate limit, the nonce and the headers are built (2026-11-01, "Hardening" above). The rate
  limit's counts live in each API instance's memory; more than one instance means more than one count.
- The page's CSP was checked in Chrome with a placeholder client id (Google's button frame loads, no
  violation on any page, Lighthouse accessibility 100 on every page with the CSP on). A sign-in with a
  real Google account under it is owner step 5.
- Follow-ups: a cap on failed device activations across addresses (decision 16); a store shared by API
  instances for the rate limit, if there is ever more than one; CSP violation reporting
  (`report-to`) once there is somewhere to send reports.
- Exclusions, redaction patterns and telemetry are not policy fields yet (ST-047), so the editor does
  not show them; the page says so.
- Local development needs a browser that keeps Secure cookies on `http://localhost` (Chrome, Edge and
  Firefox do).
