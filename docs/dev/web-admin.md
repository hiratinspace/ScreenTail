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

1. **Technicians and seats → Invite a technician**: the code is shown once. Copy it.
2. On a Windows PC with the client, **Settings → This device**, enter the code. Within fifteen seconds
   (or on **Refresh**) the device appears under Technicians and devices, and the seat count goes up.
3. **Policy**: change retention or lock local-only and **Save policy**. Devices pick it up within the
   hour (ST-047's sync).

Vite's proxy puts the page and the API on one origin, which the session cookie needs: it is host-only,
`Secure` and `SameSite=Strict`. Chrome, Edge and Firefox keep a `Secure` cookie on `http://localhost`;
Safari does not, so use one of the others locally. In a deployment, serve the built `web/dist` and the
API from one `https://` origin, with a reverse proxy sending `/v1` to the API.

## 4. Checks

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
Integrations and Download client.
