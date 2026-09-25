# Getting one real session to produce one real note

**What this is for.** ScreenTail has never drafted a note from a session. Every piece exists and is
tested — capture, redaction, the bundle, the sender, the endpoint, the model call — and the whole of it
has never run in one go. This is the step list for the first time, and for every time after that until
there is an installer.

**What it proves, and what it does not.** At the end of this you will have a note in the store, written
by a model, from a session you recorded. That is M1. It does not prove the note is any *good* — that is
the eval corpus (ST-062) and a technician's judgement — and it does not prove any of it is publishable,
because there is nowhere to publish to yet (ST-077, ST-078).

---

## Where everything runs: the laptop

**All of it runs on the laptop — the backend included.** Two facts decide that:

- The client refuses to send a bundle to a backend that is not HTTPS. That is the egress guard doing its
  job (INV-8), not a setting to change.
- The only certificate the laptop trusts without ceremony is its own ASP.NET Core development
  certificate, and that certificate is issued for `localhost` only.

So the backend runs on the laptop and the client reaches it as `https://localhost:5001`. Running the
backend on the Mac would need a certificate the laptop trusts for the Mac's name, which is more machinery
than the first run deserves.

One consequence worth stating: **the Gemini key in the Mac's user secrets is the Mac's.** User secrets
live in the user profile of the machine they were set on. The laptop needs its own copy, set once in
step 2. Read the value off the Mac without retyping it from memory:

```bash
cd backend/src/ScreenTail.Api && dotnet user-secrets list   # prints values; mind the shoulder
```

## Before you start

| You need | Why |
|---|---|
| The laptop, signed in, with you at the keyboard | The service is Windows-only and the session is a real one |
| The .NET 10 SDK on it | `dotnet --version` should say `10.x`. `setup-test-laptop.ps1` installed it for the runner; if a fresh shell cannot find it, `winget install Microsoft.DotNet.SDK.10` |
| Your own clone of the repository | Anywhere outside the runner's `_work` folder, for example `C:\src\ScreenTail`. The runner's checkout is the runner's |
| PostgreSQL on the laptop | The API refuses to start without one. Step 0 |
| The Gemini key | The free tier is enough: 20 requests a day, and this needs one |

The free tier genuinely suffices. Billing matters for *measuring* the model (thinking-token budget,
repair rate) and not for proving the loop.

Steps 0 to 2 are done once. Steps 3 onward are done every time.

---

## 0. Install PostgreSQL (once)

```powershell
winget install --id PostgreSQL.PostgreSQL.17 -e
```

The installer asks for a password for the `postgres` user. Write it down; it goes into the connection
string in step 3. Everything else can be left at its default. (If Docker Desktop is already on the
laptop, `docker run --rm -d -p 5432:5432 -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=screentail --name screentail-db postgres:17-alpine`
does the same job and the password is `dev`.)

## 1. Trust the development certificate (once)

```powershell
dotnet dev-certs https --trust
```

Windows asks whether to install the certificate. Yes. Without this the service's HTTPS request to the
backend fails certificate validation and the outbox reports a send failure rather than a note.

## 2. Save the two secrets (once)

```powershell
cd C:\src\ScreenTail\backend\src\ScreenTail.Api
dotnet user-secrets set "Summarization:ApiKey" "<the Gemini key>"
dotnet user-secrets set "Jwt:SigningKey" ([Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48)))
dotnet user-secrets list
```

The last line prints both values, so mind who is looking. The signing key has no default and the service
refuses to start without one, deliberately: a development default becomes a production key the first time
somebody forgets.

## 3. Open the backend shell (every time)

```powershell
cd C:\src\ScreenTail\backend
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'https://localhost:5001;http://localhost:5000'
$env:ConnectionStrings__Postgres = 'Host=localhost;Database=screentail;Username=postgres;Password=<from step 0>'
```

Each line matters:

- **`Development`** is not the default. The API project has no `launchSettings.json`, so without this
  the host runs as Production: it reads no user secrets, so it has no key and no signing key, and
  `--enrol-dev-device` refuses. The symptom is the service starting and then saying nothing useful.
- **The URLs** are set so the address is known rather than defaulted.
- **The connection string is an environment variable**, not a user secret, because `dotnet ef` reads it
  from the environment.

## 4. Create the database (once, and after any new migration)

```powershell
dotnet tool install --global dotnet-ef --version 10.0.12
dotnet ef database update --project src\ScreenTail.Api --startup-project src\ScreenTail.Api
```

This creates the `screentail` database if it is not there and applies every migration. The first line
fails harmlessly if the tool is already installed.

## 5. Enrol a device (whenever the token has expired)

There is no enrolment flow yet — ST-010 will issue tokens as part of signing in. Until then, in the
shell from step 3:

```powershell
dotnet run --project src\ScreenTail.Api -- --enrol-dev-device
```

It prints a tenant, a device, an expiry and a line you can paste:

```
SCREENTAIL_DEVICE_TOKEN=eyJhbGciOi...
```

**The token lasts an hour.** Run this again when it expires; it re-uses the same device rather than
seeding another, so it will not leave a trail of credentials behind. It refuses outright unless the API
is in Development — a control rather than a warning.

## 6. Start the backend

Same shell:

```powershell
dotnet run --project src\ScreenTail.Api
```

Leave it running. In a second PowerShell window, check it is alive over HTTPS:

```powershell
Invoke-RestMethod https://localhost:5001/health
```

`status ok` means the certificate is trusted and the service is listening. A certificate error here means
step 1 did not complete.

## 7. Point the client at it and start it

In that second window:

```powershell
cd C:\src\ScreenTail
$env:SCREENTAIL_BACKEND = 'https://localhost:5001'
$env:SCREENTAIL_DEVICE_TOKEN = '<the token from step 5>'
powershell -ExecutionPolicy Bypass -File .\scripts\windows\run-local.ps1
```

It has to be `https://`. An `http://` address is refused by the guard with "http is not encrypted", and
the outbox keeps the work rather than sending it.

Both values are environment variables and neither is a settings system. ST-047 and ST-081 own settings;
this is what makes the path runnable before they exist. The address you set is also what names the
backend host in the egress allowlist, so a typo here looks like a blocked request rather than a request
to somewhere unexpected — which is the intended behaviour, not a bug to work around.

## 8. Record something worth drafting

Open a remote-desktop window — RDP, ScreenConnect, anything in the registry — so capture starts by
itself, or press **Ctrl+Alt+R** with the window you want in front. Then **do a small real job**: break
something, find it, fix it. Say out loud what you are doing, because narration is half of what the note
is written from.

Two or three minutes is plenty. A session of someone opening a console and closing it again produces a
note that says exactly that.

Then press **Ctrl+Alt+S** to stop and draft.

## 9. See whether it worked

A note arrives through the outbox rather than immediately: the session finalises, the work is queued, and
the queue drains in the background. Give it a few seconds.

**In the UI:** the status badge in the shell window changes to *Draft ready* and a tray notification
names the draft. Click the notification, or the tray icon's Open: the shell opens on Review with the
note on the left and the screenshots beside it. History lists the session too, and a double-click on any
row opens it. This is the first time these panes will have been seen over a real session — CI has only
rendered them over a fixture — so anything odd about them is worth writing down in step 10.

**In the service window**, the interesting lines are the bundle report (counts and sizes only — nothing
that was on the screen) and whatever the outbox says about sending.

**In the backend window**, one request to `/v1/sessions/summarize` and, on the first run, the model's
own timing.

**If nothing happens**, the failure is almost certainly one of these, and each says so plainly:

| What you see | What it means |
|---|---|
| "http is not encrypted" | `SCREENTAIL_BACKEND` starts with `http://`. It must be `https://` (step 7) |
| A certificate error in the service window | Step 1 was skipped or declined |
| "This device is not enrolled yet" | `SCREENTAIL_DEVICE_TOKEN` is unset or expired — step 5 again |
| "No summarization provider is configured" | `SCREENTAIL_BACKEND` is unset in the shell that started the client |
| 501 `not_configured` from the backend | The API is running without the key: usually `ASPNETCORE_ENVIRONMENT` was not set, so user secrets were never read (step 3) |
| Connection refused | The backend is not running, or is not on port 5001 (step 6) |
| A blocked-host message | The address in step 7 does not match what the guard was told to allow |
| "reached its drafting budget for today" | The daily cap, which is $0.10 a session and $10 a day by default |
| 401 from the backend | The token expired. It lasts an hour |

## 10. Write down what actually happened

The first run is evidence, so record it: how long the model took, what it cost (the backend log says),
how many frames went, and — the only question that matters — **whether the note describes what you
did**. That answer is the first row of the eval corpus.

If it does, that is M1, and the next thing is the eval corpus rather than more plumbing.

---

## What is deliberately missing

- **Enrolment** (ST-010). Step 5 exists because it does not.
- **Settings** (ST-047, ST-081). Step 7 is two environment variables for the same reason.
- **Hosting** (ST-007). The backend runs on the laptop; Phase D deploys it, and the certificate question
  above goes away with a real hostname.
- **Publishing** (ST-077, ST-078). The note stays in the store. There is nowhere to send it yet.

Each of those is a ticket rather than an oversight, and this document should shrink as they land.

## Enrolling the real way (ST-010, 2026-09-25)

`--enrol-dev-device` still works in Development. The production path is an invite:

```bash
dotnet run --project backend/src/ScreenTail.Api -- --invite "new:Acme IT:5" t.ortiz@acme.example "T. Ortiz"
# prints the tenant id and, once, a code like  KX7PM-4R2WQ  (72 hours, one device)
curl -X POST "$BACKEND/v1/devices/activate" -H "Content-Type: application/json" \
  -d '{"code":"KX7PM-4R2WQ","deviceName":"TECH-LAPTOP"}'
# → tenantName, deviceId, refreshToken (keep it), accessToken (an hour), expiresAt
curl -X POST "$BACKEND/v1/devices/token" -H "Content-Type: application/json" -d '{"refreshToken":"…"}'
```

The client does not do this itself yet; until it does, put the access token in `SCREENTAIL_DEVICE_TOKEN`
as before. `--offboard-tenant <id>` deletes every row of a tenant.
