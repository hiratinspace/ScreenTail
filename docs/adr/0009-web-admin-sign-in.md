# ADR-0009: Owner sign-in to the web admin, with Google

- **Status:** **Proposed 2026-10-31** (ST-099). Built and tested on the Mac suite. **The owner chose
  Google** as the way owners sign in: not email and password, and not Microsoft. Nothing is switched on
  until the operator creates a Google OAuth client id and sets it (below, "Owner steps"). The owner
  decides the defaults listed under "Decisions for the owner" before this leaves Proposed.
  **Hardened 2026-11-01:** the three items this record first listed as owed (a rate limit on the
  anonymous routes, a single-use sign-in nonce, and a Content-Security-Policy with related headers) are
  built and tested; "Hardening" below has the design. **Invite-code guessing addressed 2026-11-01**
  (decision 16, the follow-up it named): invite codes are 20 characters (about 99 bits), and refused codes
  across all addresses raise a warning for the operator; "Guessing invite codes" below has the analysis.
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
256 bits and every device behind the MSP's one address renews hourly). A reflective test
(`EveryAnonymousRouteIsCountedButTheOnesExemptOnPurpose`) fails if an anonymous route appears without the
limit. **A call another site's page started is refused before it is counted**: a limited route answers
`Sec-Fetch-Site: cross-site` with 403 and no body, so a page anywhere cannot spend an office's allowance
from its visitors' browsers. The admin's page is same-origin, and a device sends no such header.

The client address is the connection's. **No forwarded header is read unless the operator lists the
proxy** (`ForwardedHeaders:KnownProxies:0`, `ForwardedHeaders:KnownNetworks:0` in CIDR form, both empty
by default); then `X-Forwarded-For` and `X-Forwarded-Proto` are honoured only on connections from those
peers, only the last entry (the one the listed proxy wrote, `ForwardedHeaders:ForwardLimit`, default 1)
counts, and ASP.NET Core's own default of trusting loopback is cleared. These options are applied after
all other configuration, so `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (with which the framework runs the
middleware itself and, with empty lists, would trust every caller) forwards nothing unless a proxy is
listed. Without the list, behind a proxy every caller shares the proxy's count, which is why listing it
is an owner step for any hosted deployment. The counts are in memory, per API instance. A connection
with no address (a Unix socket) counts in one shared partition, and with a proxy listed the framework
also honours forwarded headers on such a connection, so a deployment that puts the API on a socket
should let only its proxy reach that socket.

**2. A single-use nonce in every Google ID token** (`SignInNonces`, table `admin_sign_in_nonces`). Before
drawing Google's button the page asks `POST /v1/admin/session/nonce` for one and passes it to
`google.accounts.id.initialize({ nonce })`; Google signs the value into the token's `nonce` claim.
Sign-in and signup accept a token only when that claim is a nonce **made here, not expired and not
used**; missing, malformed, forged, expired and used are all the same 401 as a bad signature.

*Signed when handed out, stored only when used.* A nonce is a version byte, its expiry and 128 random
bits, followed by an HMAC-SHA256 over them (76 URL-safe characters). The MAC key is derived like the
admin session's, HMAC-SHA256 of its own label `screentail-admin-sign-in-nonce-key-v1` under
`Jwt:SigningKey`, so it is neither the devices' key nor the admin session's (a nonce made under either is
refused, and changing the label voids every nonce handed out). Checking one is the MAC, compared in
constant time, and an expiry that has not passed and is no further off than one lifetime: no lookup, on
any instance. **Handing nonces out writes nothing**, so the anonymous route cannot fill a table however
many addresses call it. **A use writes the nonce's SHA-256 as the table's primary key**, in the same save
as the sign-in time or the new tenant and owner; a second use of the same nonce is a key conflict, so of
two requests with one token exactly one wins on any number of instances, and a signup whose save fails
for another reason leaves its nonce unused for the owner to retry. Rows grow only with real sign-ins,
and each use deletes the rows whose nonces expired more than one lifetime ago (not at expiry, so an
instance whose clock runs behind still finds the record that refuses a replay). Learning that an account may create a tenant
(`needs_tenant`) does not use the nonce, so the first-run screen sends the same token on; a refused
organisation name does not either. Lifetime **10 minutes** (`Admin:SignInNonceLifetime`, 1 to 60
minutes), and the answer carries `expiresInSeconds` so the page draws a fresh button a minute before it
runs out without trusting its own clock. The page also takes the button away the moment Google hands
back a token (its nonce is spent) and whenever no fresh nonce can be had, and after a 429 asks again
once its `Retry-After` has passed.

*Why not a cookie, or memory.* Single use has to hold across a restart and across more than one API
instance: a store in memory forgets on restart and differs between instances, and a signed cookie cannot
be "used up" in the copy an attacker captured with the token. The table belongs to no tenant, because a
nonce is used before the tenant exists. Binding the nonce to the browser as well (a cookie holding it,
compared at sign-in) would stop a token being redeemed from another browser inside the nonce's ten
minutes; it is listed for the owner below rather than built.

**3. Security headers.** Every API answer, refusals and errors included (added as headers are sent,
and an exception nobody caught is turned into a bare 500 by an exception handler right after, so the
server never writes one without them; a body the endpoint could not bind keeps its 400 or 415), carries `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none';
form-action 'none'`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy:
no-referrer`, `Cross-Origin-Resource-Policy: same-origin` and a Permissions-Policy that turns off the
camera, microphone, location, payment and the rest (`SecurityHeaders`). The API serves JSON only, so
nothing in it needs any source. They are defaults, added only where the answer has none: the demo PSA's
page (Development only, ST-090) keeps its own CSP, which lets its inline styles in.

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

### Guessing invite codes (built 2026-11-01)

Decision 16 left one door open: activation is limited per address, so an attacker with many addresses
guesses in parallel. The numbers, before and after.

**The code as it was generated.** Ten characters from a 31-character alphabet (no 0, 1, I, L or O), each
taken as a random byte modulo 31. 256 is 8 x 31 + 8, so the first eight characters (A to H) came up with
probability 9/256 and the other twenty-three with 8/256. That is 4.952 bits a character and **49.5 bits
a code** (Shannon; a uniform draw would give 49.54), and a min-entropy of **48.3 bits**: a guesser who
tries the likeliest codes first gains at most a factor of 2.3.

**How many codes a guess is against.** Activation looks the code up by its hash across the whole
`invites` table (one unique index), not inside a tenant, so every guess is a guess at **every tenant's
outstanding invites at once** (not used, not expired). Nothing caps the invites a tenant may have
outstanding. A pilot has a handful; a thousand tenants with a few pending each is thousands. Below, N is
100, 1,000 and 10,000.

**How fast one can guess.** 10 calls a minute per partition is 43,200 per address over a code's 72 hours.
IPv4 needs a botnet for scale. **IPv6 does not:** a partition is a /64, a hosting customer is routinely
given a /48, which is 65,536 partitions (2.8 billion guesses in 72 hours from one allocation), and a /32
is four billion, at which point the bound is how many requests the API can serve, not the limit.

**The chance of at least one hit within one 72-hour lifetime**, 1 - exp(-G x N / 2^48.3), G the guesses:

| Addresses (partitions) | Guesses in 72 h | N = 100 | N = 1,000 | N = 10,000 |
|---|---|---|---|---|
| 1 | 43,200 | 1.2e-8 | 1.2e-7 | 1.2e-6 |
| 1,000 | 4.3e7 | 1.2e-5 | 1.2e-4 | 0.12% |
| 100,000 (a botnet, or one and a half IPv6 /48s) | 4.3e9 | 0.12% | 1.2% | 11.7% |
| 1,000,000 | 4.3e10 | 1.2% | 11.7% | 71% |

The run can be repeated every 72 hours against fresh codes. A hit enrols a device in a tenant chosen by
luck: it takes a seat, reads the policy, publishes, and (ST-009) manages the tenant's PSA and Hudu
credentials. **A copy of the database was a second route:** codes are kept as unsalted SHA-256, and 2^49.5
hashes is about 22 hours on one GPU at ten billion a second, inside a code's life.

**After: twenty characters, drawn uniformly, about 99.1 bits** (`Invites`, 20 x log2 31). The worst row
above becomes 6.4e-16. An attacker the API could not even serve, ten thousand guesses a second for a year
(3.2e11) against a million live codes, succeeds with probability 4.7e-13. Searching a copy of the table is
2^99 hashes, about 2e12 years at the same rate. No lockout is needed to get there, so there is no lockout
for an attacker to turn against a real technician.

**What was weighed, and why each was taken or not:**

1. **A longer code: built.** Twenty characters from the same alphabet in four groups of five,
   `XXXXX-XXXXX-XXXXX-XXXXX`: the groups people already read out over the phone, shorter than a Windows
   product key (25). Dashes, spaces and case are still forgiven on both sides. Drawn with
   `RandomNumberGenerator.GetString`, which rejects rather than folds the values past a multiple of 31, so
   every character is equally likely (a test counts 400,000 of them). 16 characters is 79.3 bits, under
   the 80 aimed for; 18 (89.2 bits) would also clear the year-long bound but splits into awkward groups;
   20 keeps the fives with ten bits to spare beyond 18. The length is an owner decision (21 below).
2. **A split code with a per-invite lockout: not built.** A counter of wrong attempts per invite needs a
   public part the server looks up before it checks the secret. If that part is short enough to be found,
   enumerating it finds live invites, and N wrong secrets against each lock them: a way to stop a
   tenant's technicians activating, invite by invite, for N calls apiece. If it is long enough not to be
   found, a guesser never reaches a real one and the counter never moves; it helps only somebody who
   holds the public part without the secret, which handing the whole code on does not produce. It would
   also add an oracle to close (unknown id against wrong secret) for no gain over 99 bits.
3. **A cap on failures across all addresses: not built.** Anybody can trip it by guessing badly on
   purpose, and then nobody activates. That is the denial-of-service lever decision 16 warned of.
4. **A delay on failed attempts: not built.** A guesser with many addresses has many requests in flight
   rather than one after another, so a delay does not slow it; it holds the API's connections, which is a
   cost to us, and it slows the technician who mistyped.
5. **A warning for the operator: built** (`ActivationWatch`). Every activation refused for its code
   (unknown, malformed, used or expired; not the seat limit, a switched-off tenant or a bad device name)
   is counted across all addresses, and **one warning per window** is logged once the count reaches
   `ActivationWatch:WarnAt` in `ActivationWatch:Window` (**50 in 10 minutes**; 1 to 1,000,000, and 1 minute
   to 24 hours in whole seconds, checked at startup). The line carries the count and the window, in
   exact seconds, and nothing else: no code, no address, no tenant (INV-10), and no claim about the
   codes' strength, since old ten-character codes may still be live. Nobody is refused or slowed for it. The count is per instance,
   like the rate limit's (decision 20).
6. **Uniform refusals: built.** A code that cannot be one we issued (the wrong length, a character outside
   the alphabet, nothing at all) is answered, without a lookup, with the same 400 and the same bytes as a
   well-formed code nobody issued; an empty code or one of only dashes used to reach the hash and answer
   500. The answer names no tenant, invite or person (a test seeds two tenants' invites and checks every
   refusal: unknown, used, expired, the seat limit's 409 and a switched-off tenant's 403). The activation
   body is capped at the anonymous routes' 16 KB, refused with 413 before it is read.

**Timing.** Every well-formed code costs exactly one indexed lookup by its SHA-256, whatever invites
exist, whose they would be and how many; a miss is answered with the same bytes. A malformed code skips
the lookup, which tells the caller only what it sent. The answers that differ (used, expired, the seat
limit, a switched-off tenant) differ on purpose, for the technician holding the code, and are reachable
only with a code that was issued, which guessing finds with odds of about 2^-99. Our code compares no
secret as a string: the database matches the hash of a 99-bit value, so even a measurable difference in
the index would say something about a hash rather than a code. There is nothing for a constant-time
comparison to apply to (`DeviceTokens.Matches` stays for any path that compares two values).

**Codes issued before the change** were ten characters and **are accepted until they expire**, 72 hours
at most: the lookup is by hash whatever the length, and the shape check admits both lengths. A ten-character
guess can only ever match a ten-character code, so accepting the old length makes no new code weaker;
for those 72 hours the old codes keep their old odds. Nothing is deployed yet (ST-007), so in practice
there are none. Dropping the old length once 72 hours have passed after the first deployment is one line
(decision 23).

**The client.** The Settings and onboarding card (`ActivationPanel`, `InviteCodes` in Core) takes a
twenty-character code and still an old ten-character one, says how many characters it has so far, names a
look-alike (0, 1, I, L, O) as soon as one is typed, and sends the code grouped as the invite shows it.
The box is wider and types in capitals. The pipe carries the code as a string, so the IPC contract is
unchanged. A test reads the backend's alphabet and lengths so the client's copy cannot drift. The web
admin shows the code as the backend returns it and lets it wrap on a narrow window; the operator's
`--invite` prints it unchanged. Both sides read typed codes by the same table,
`shared/contracts/invite-code.v1.json` (the alphabet, the lengths and eighteen typed codes with what each
must become), so a change to one side's reading alone, such as Unicode normalisation, fails a test.

**Client and backend ship together.** A client built before this change (the #224 MSI, and the
`demo-setup` and `demo-psa` branches) checks for exactly ten characters and refuses a twenty-character
code on the card before sending it, so a new backend's codes cannot be entered on an old client. The
client and the backend must come from the same build, and the demo laptop must be set up from `main`
once this is merged. (An old client with an old backend, or a new client with either, works.)

**Two activations at once** (fixed with this change, review of #248). Before, two requests with one code
could both read the invite as unclaimed and both add a device, and two requests for a tenant's last seat
could both count a free seat. The activation now runs in one transaction whose first statement locks the
tenant's row (`TenantLock`: the row written unchanged, which Postgres holds locked to the commit; SQLite
locks the database for its one writer). Under that lock it reads the tenant's seats and switch, claims the
invite with a conditional update (`AcceptedAt IS NULL` and unexpired at the moment of the claim, checked by
its row count), counts the seats and adds the device. A loser is told why from the row as it is then:
used, expired since the first read, or gone (a tenant offboarded meanwhile answers like an unknown code). A
seat refusal rolls the claim back, so the code works once a seat is free. The commit is not cancellable,
so a request aborted then cannot leave an activation committed with nobody holding its refresh token.
**Offboarding takes the same lock first**, so the two always lock a tenant's rows in one order. The tests
make the race happen every time, with an interceptor that runs a second activation right after the first
one's read; another pins the lock inside the transaction before the claim and the count (on SQLite the
transaction alone would serialise, so only this notices the lock missing); and two tests run the race on
a real Postgres, in a database of their own, in CI's Backend job (`PostgresActivationRaceTests`, which
fails rather than skips on CI without it).

**The rate limit stays**, as a bound on what each guess costs us rather than as the defence against
guessing.

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
16. **Guessing invite codes is answered by the code's length, not by a lockout** (built 2026-11-01,
    "Guessing invite codes" above). Activation is still limited per address only, but an invite code is
    now 20 characters drawn uniformly from 31 (about 99 bits): many addresses guessing at once stay
    hopeless, and no count across addresses refuses anybody. Refused codes across all addresses log one
    content-free warning per window. The ten-character codes (about 49 bits) this replaced gave a
    botnet-sized guesser up to a 1 in 9 chance of a hit within one code's life.

Open for the owner (raised in review, 2026-11-01; the build keeps the conservative choice and decides
none of these):

17. **One budget or two for an office behind one address.** The nonce, sign-in, signup and device
    activation share the 10 a minute. An MSP activating many technicians at once from the office could
    use it up and lock the owner out of sign-in for a minute; activation could get a budget of its own.
18. **How wide an IPv6 caller is.** Counted by its /64 today. Some providers hand a subscriber a /56 or
    a /48, which lets one subscriber count as up to 256 or 65,536 callers.
19. **Binding the nonce to the browser** with a cookie as defence in depth (see 2 above).
20. **Counts in memory, per instance.** Fine for one API instance; more than one means more than one
    allowance per address unless the counts move to a shared store. The activation warning (below)
    counts per instance too.

Open for the owner (invite-code guessing, 2026-11-01; built with the conservative choice):

21. **An invite code's length: 20 characters, about 99 bits.** 18 (about 89 bits) would also clear the
    worst case worked above, in groups of six; 16 (79 bits) would not reach 80. Longer is safer and only
    slower to type.
22. **When the operator is warned: 50 refused codes in 10 minutes**, all addresses together, per
    instance (`ActivationWatch:WarnAt`, `ActivationWatch:Window`). It is a log line only: there is no
    alerting channel until hosting exists (ST-007).
23. **Old ten-character codes accepted until they expire.** Nothing is deployed, so there should be none,
    and the old length could be dropped from the shape check now; otherwise it can go once 72 hours have
    passed after the first deployment. Kept for now as the choice that refuses no code anybody holds.

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
- Invite-code guessing across many addresses is answered (2026-11-01, decision 16): 99-bit codes, uniform
  refusals and a warning for the operator, with no lockout. A per-invite lockout and a cap across
  addresses were weighed and not built ("Guessing invite codes").
- Follow-ups: a store shared by API instances for the rate limit and the activation warning, if there is
  ever more than one (decision 20); dropping the old ten-character code length after the first deployment
  (decision 23); CSP violation reporting (`report-to`) once there is somewhere to send reports.
- The device client turns the 429 into "Too many activation attempts from this network. Wait a minute
  and try again." on the Settings card (`DeviceSession`).
- Exclusions, redaction patterns and telemetry are not policy fields yet (ST-047), so the editor does
  not show them; the page says so.
- Local development needs a browser that keeps Secure cookies on `http://localhost` (Chrome, Edge and
  Firefox do).
