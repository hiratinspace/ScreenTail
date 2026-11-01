# The web admin: running it, and switching on Google sign-in

ST-099. The owner of a tenant signs in with Google, names the organisation on the first sign-in, invites
technicians, sets the policy, and sees devices activate. How it works and why is ADR-0009; this page is
how to run it.

## 1. Create the Google OAuth client id (once per deployment)

1. Open Google Cloud Console, pick or create a project, and go to **APIs & Services → OAuth consent
   screen**. Choose **External** (or Internal for a Workspace-only deployment), give the app a name
   ("ScreenTail admin") and a support address. No scopes beyond the default `openid`, `email` and
   `profile` are needed. While the consent screen is in **Testing**, add each owner's Google address
   under Test users.
2. Go to **APIs & Services → Credentials → Create credentials → OAuth client ID**, application type
   **Web application**.
3. Under **Authorized JavaScript origins** add every origin the admin page is served from:
   - local work: `http://localhost:5173` (the dev server), `http://localhost:4173` (`vite preview`, which the
     Lighthouse check below uses) and `http://localhost` (Google asks for the bare one too for local pages);
   - a deployment: its `https://` origin, for example `https://admin.example.com`.

   Leave **Authorized redirect URIs** empty: the page uses the pop-up flow and nothing is redirected.
4. Create it and copy the **Client ID** (it ends in `.apps.googleusercontent.com`). The client secret
   Google also shows is **not used anywhere**: ignore it, and never put it in configuration or in the
   repository. The client id is not a secret; the browser is given it.

## 2. Configuration

Backend (environment variables, or `dotnet user-secrets` in Development):

| Key | Default | What it does |
|---|---|---|
| `Admin__GoogleClientId` | empty | The OAuth client id. Empty switches owner sign-in off: both sign-in routes answer 501 and say which setting is missing. |
| `Admin__SignupAllowlist__0`, `__1`, ... | empty | Google addresses allowed to create a tenant on their first sign-in. Empty means nobody can sign up. Existing owners sign in regardless. |
| `Admin__NewTenantSeats` | `5` | Seats a tenant created by signup starts with (1 to 1000). |
| `Admin__SessionLifetime` | `08:00:00` | How long an admin session lasts (5 minutes to 24 hours). |
| `Admin__ClientDownloadUrl` | empty | The Windows installer's `https://` address for Download client. Empty shows "no download yet". |
| `Admin__SignInNonceLifetime` | `00:10:00` | How long a sign-in nonce is accepted after the page asks for it, first-run screen included (1 to 60 minutes). Nonces are signed with a key derived from `Jwt__SigningKey`, so changing that key voids the ones handed out. |
| `RateLimit__PermitLimit` | `10` | Calls one client address may make in a window to the sign-in nonce, sign-in, signup and device activation, together (1 to 100000). Sign-out is never counted. |
| `RateLimit__Window` | `00:01:00` | That window (1 second to 1 hour). The call past the limit gets 429 with `Retry-After` and no body. |
| `ActivationWatch__WarnAt` | `50` | Device activations refused for their invite code (unknown, malformed, used or expired), from all addresses together, at which one warning is logged per window (1 to 1000000). Nobody is refused or slowed for it. |
| `ActivationWatch__Window` | `00:10:00` | That window, in whole seconds (1 minute to 24 hours). The warning carries the count and the window in seconds, never a code, an address or a tenant. |
| `ForwardedHeaders__KnownProxies__0`, `__1`, ... | empty | The reverse proxies in front of the API, as IP addresses. Only a connection from one of them has its `X-Forwarded-For` and `X-Forwarded-Proto` read. Empty: no forwarded header is read from anybody. |
| `ForwardedHeaders__KnownNetworks__0`, ... | empty | The same, as networks in CIDR form (`10.0.0.0/24`). |
| `ForwardedHeaders__ForwardLimit` | `1` | How many listed proxies in a row, counted back from the API (1 to 5). |

The backend's other settings (`ConnectionStrings__Postgres`, `Jwt__SigningKey`) are as in
`backend/README.md`. `Jwt__Audience` must not be `screentail-admin`, which is the admin session's own
audience; startup refuses it.

Web build (Vite reads `VITE_*` from the environment or from `web/.env.local`, which git ignores):

| Key | What it does |
|---|---|
| `VITE_GOOGLE_CLIENT_ID` | The same client id. Without it the sign-in page says "Sign-in is not configured" instead of drawing Google's button. |
| `SCREENTAIL_API` | Development only: where Vite's proxy sends `/v1`. Default `http://localhost:5000`. |

## 3. Run it locally

```bash
# The backend, as backend/README.md says, plus the admin settings:
docker run --rm -d -p 5432:5432 -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=screentail --name screentail-db postgres:17-alpine
cd backend
export ConnectionStrings__Postgres='Host=localhost;Database=screentail;Username=postgres;Password=dev'
export Jwt__SigningKey="$(openssl rand -base64 48)"
export Admin__GoogleClientId='<your client id>.apps.googleusercontent.com'
export Admin__SignupAllowlist__0='you@yourcompany.example'
dotnet ef database update --project src/ScreenTail.Api --startup-project src/ScreenTail.Api
dotnet run --project src/ScreenTail.Api          # http://localhost:5000

# The web app, in a second terminal:
cd web
npm ci
VITE_GOOGLE_CLIENT_ID='<your client id>.apps.googleusercontent.com' npm run dev   # http://localhost:5173
```

Open `http://localhost:5173`, sign in with an address on the allowlist, name the organisation, then:

1. **Technicians and seats → Invite a technician**: the code is shown once. Copy it. It is twenty
   letters and digits in four groups of five (`XXXXX-XXXXX-XXXXX-XXXXX`, about 99 bits, so it cannot be
   guessed from any number of addresses; ADR-0009, "Guessing invite codes"). Dashes, spaces and case do
   not matter when it is typed. Codes issued before 2026-11-01 were ten characters and work until they
   expire. **A client built before then refuses a twenty-character code on the card**, so run a client
   and a backend from the same build (the demo laptop from `main`).
2. On a Windows PC with the client, **Settings → This device**, enter the code. Within fifteen seconds
   (or on **Refresh**) the device appears under Technicians and devices, and the seat count goes up.
3. **Policy**: change retention or lock local-only and **Save policy**. Devices pick it up within the
   hour (ST-047's sync).

The sign-in page asks the backend for a one-time nonce (`POST /v1/admin/session/nonce`) before it draws
Google's button, so the backend must be up for the button to appear; without it the page says it could
not reach the server. Ten sign-in calls a minute from one address is the default limit: a developer
signing in and out repeatedly can set `RateLimit__PermitLimit` higher locally.

Vite's proxy puts the page and the API on one origin, which the session cookie needs: it is host-only,
`Secure` and `SameSite=Strict`. Chrome, Edge and Firefox keep a `Secure` cookie on `http://localhost`;
Safari does not, so use one of the others locally. In a deployment, serve the built `web/dist` and the
API from one `https://` origin, with a reverse proxy sending `/v1` to the API.

## 4. Security headers

The API sets its own on every answer (`SecurityHeaders`: `default-src 'none'`, no framing, `nosniff`,
no referrer). The page's are written once, in `web/security-headers.ts`, and ADR-0009 says why each is
there:

- `npm run dev` sends all of them **except the Content-Security-Policy**, because Vite's dev page runs
  inline scripts and styles for hot reload.
- `vite preview` sends **all of them**, so the built page and a local Lighthouse run see the real policy.
- **A deployment's web server must send them with the page**, exactly as `appHeaders` in that file says.
  The deployment in `deploy/` (ST-007, `docs/dev/deploy.md`) does this without a copy: its Caddy
  snippet is generated from this file when the proxy image is built, and `deploy/smoke.sh` compares
  what the running proxy sends with it. Today they are:

```
Content-Security-Policy: default-src 'none'; script-src 'self' https://accounts.google.com/gsi/client; style-src 'self' 'unsafe-inline' https://accounts.google.com/gsi/style; img-src 'self'; font-src 'self'; connect-src 'self' https://accounts.google.com/gsi/; frame-src https://accounts.google.com/gsi/; manifest-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'
X-Content-Type-Options: nosniff
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: accelerometer=(), autoplay=(), bluetooth=(), camera=(), display-capture=(), encrypted-media=(), geolocation=(), gyroscope=(), hid=(), magnetometer=(), microphone=(), midi=(), payment=(), screen-wake-lock=(), serial=(), usb=(), xr-spatial-tracking=()
Cross-Origin-Opener-Policy: same-origin-allow-popups
X-Frame-Options: DENY
```

`'unsafe-inline'` is for styles only: Google's sign-in script inserts a style element and style
attributes, and without it Chrome draws the button unstyled. Nothing in the page needs an inline
script; `src/securityHeaders.test.ts` builds the app and fails if one appears.

**Behind a reverse proxy,** list it in `ForwardedHeaders__KnownProxies__0` (or `__KnownNetworks__0`) and
have it set `X-Forwarded-For` itself. Otherwise every owner and technician counts as the proxy's one
address for the rate limit, and one person's ten calls a minute lock everyone else out of sign-in.

Two edges to know about:

- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` does not switch forwarding on by itself. With no proxy
  listed the API reads no forwarded header from anybody, whatever that variable says. With a proxy
  listed, leave the variable unset: set as well, the forwarded headers are processed twice, so a client
  address inside a listed proxy network would be trusted as a proxy too. Since ST-007 the API refuses
  to start with both.
- A connection with **no address** (the API listening on a Unix socket) counts in one shared partition
  for the rate limit, and once a proxy is listed the framework also honours `X-Forwarded-For` on such a
  connection. If the API is put on a socket, make sure only the proxy can reach that socket.

A call to the limited routes that a browser marks `Sec-Fetch-Site: cross-site` (another site's page) is
refused with 403 before it is counted. The admin page is same-origin, so it never sees this.

The demo PSA's page (`/dev/demo-psa`, Development only) sends its own CSP, which allows its inline
styles; the API's closed default is added only to answers that set none.

## 5. Checks

```bash
cd web && npm run lint && npm run typecheck && npm test && npm run build
cd backend && dotnet test ScreenTail.Backend.sln     # the Admin tests are tests/ScreenTail.Api.Tests/Admin
```

No test calls Google: the backend's tests replace Google's key set with keys they generate, and the web
tests replace the API and Google's button.

**Lighthouse accessibility** (the ticket asks for 95 or more): build with a client id, run
`npx vite preview` in `web/` with the backend up, and run Lighthouse's accessibility category against
`http://localhost:4173/`. The signed-in pages need the session cookie in the browser Lighthouse drives
(sign in first in that profile, or set the cookie with Puppeteer and pass the page to Lighthouse's Node
API). On 2026-10-31 every page scored 100 in light and dark: sign-in, Technicians and seats, Policy,
Integrations and Download client. On 2026-11-01, with the CSP on (`vite preview`) and a stand-in for the
backend's admin routes, every page scored 100 again and Chrome reported no CSP violation, Google's button
frame included.
