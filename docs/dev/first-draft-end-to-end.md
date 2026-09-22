# Getting one real session to produce one real note

**What this is for.** ScreenTail has never drafted a note from a session. Every piece exists and is
tested — capture, redaction, the bundle, the sender, the endpoint, the model call — and the whole of it
has never run in one go. This is the runbook for the first time, and for every time after that until
there is an installer.

**What it proves, and what it does not.** At the end of this you will have a note in the store, written
by a model, from a session you recorded. That is M1. It does not prove the note is any *good* — that is
the eval corpus (ST-062) and a technician's judgement — and it does not prove any of it is publishable,
because there is nowhere to publish to yet (ST-077, ST-078).

---

## Before you start

| You need | Why |
|---|---|
| The laptop, signed in | The service is Windows-only |
| A Postgres for the backend | The API refuses to start without one |
| A Gemini API key | The free tier is enough: 20 requests a day, and this needs one |

The free tier genuinely suffices. Billing matters for *measuring* the model (thinking-token budget, repair
rate) and not for proving the loop.

---

## 1. Start the backend

**The Gemini key is already saved** in user secrets as `Summarization:ApiKey`, and Development reads
those automatically. Check without printing it:

```bash
cd backend
dotnet user-secrets list --project src/ScreenTail.Api
```

If it is there, do not set it again. What is *not* saved is the database and the signing key:

```bash
export ConnectionStrings__Postgres='Host=localhost;Database=screentail;Username=postgres;Password=postgres'
export Jwt__Issuer=screentail-dev Jwt__Audience=screentail-api
export Jwt__SigningKey='a-development-signing-key-at-least-32-chars'
dotnet run --project src/ScreenTail.Api
```

Those two could live in user secrets as well, which is the better home for them — the environment
variables above are what makes the first run possible without deciding that.

The signing key has no default and the service refuses to start without one, deliberately: a development
default becomes a production key the first time somebody forgets.

Check it is alive:

```bash
curl -s localhost:5000/health
```

## 2. Enrol a device

There is no enrolment flow yet — ST-010 will issue tokens as part of signing in. Until then:

```bash
dotnet run --project src/ScreenTail.Api -- --enrol-dev-device
```

It prints a tenant, a device, an expiry and a line you can paste:

```
SCREENTAIL_DEVICE_TOKEN=eyJhbGciOi...
```

**The token lasts an hour.** Run this again when it expires; it re-uses the same device rather than
seeding another, so it will not leave a trail of credentials behind.

It refuses outright unless the API is in Development. That is a control rather than a warning — starting
the production container with this flag still on a command line is an accident somebody will eventually
have.

## 3. Point the laptop at it

On the laptop, in the shell that will start the service:

```powershell
$env:SCREENTAIL_BACKEND = 'http://<your machine>:5000'
$env:SCREENTAIL_DEVICE_TOKEN = '<the token from step 2>'
.\scripts\windows\run-local.ps1
```

Both are environment variables and neither is a settings system. ST-047 and ST-081 own settings; this is
what makes the path runnable before they exist.

The address you configure is also what names the backend host in the egress allowlist, so a typo here
looks like a blocked request rather than a request to somewhere unexpected — which is the intended
behaviour, not a bug to work around.

## 4. Record something worth drafting

Open a remote-desktop window — RDP, ScreenConnect, anything in the registry — so capture starts by
itself, and then **do a small real job**: break something, find it, fix it. Say out loud what you are
doing, because narration is half of what the note is written from.

Two or three minutes is plenty. A session of someone opening a console and closing it again produces a
note that says exactly that.

Then press **Ctrl+Alt+S** to stop and draft.

## 5. See whether it worked

A note arrives through the outbox rather than immediately: the session finalises, the work is queued, and
the queue drains in the background. Give it a few seconds.

**In the UI:** the session moves to *Draft ready* in History, and Review shows the note.

**In the service log**, the interesting lines are the bundle report (counts and sizes only — nothing that
was on the screen) and whatever the outbox says about sending.

**If nothing happens**, the failure is almost certainly one of these, and each says so plainly:

| What you see | What it means |
|---|---|
| "This device is not enrolled yet" | `SCREENTAIL_DEVICE_TOKEN` is unset or expired — step 2 again |
| "No summarization provider is configured" | `SCREENTAIL_BACKEND` is unset on the laptop |
| A blocked-host message | The address in step 3 does not match what the guard was told to allow |
| "reached its drafting budget for today" | The daily cap, which is $0.10 a session and $10 a day by default |
| 401 from the backend | The token expired. It lasts an hour |

## 6. Write down what actually happened

The first run is evidence, so record it: how long the model took, what it cost (the log says), how many
frames went, and — the only question that matters — **whether the note describes what you did**.

If it does, that is M1, and the next thing is the eval corpus rather than more plumbing.

---

## What is deliberately missing

- **Enrolment** (ST-010). Step 2 exists because it does not.
- **Settings** (ST-047, ST-081). Step 3 is two environment variables for the same reason.
- **Hosting** (ST-007). The backend runs on your machine; Phase D deploys it.
- **Publishing** (ST-077, ST-078). The note stays in the store. There is nowhere to send it yet.

Each of those is a ticket rather than an oversight, and this document should shrink as they land.
