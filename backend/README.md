# ScreenTail backend

ASP.NET Core minimal API on .NET 10: tenants, technicians, devices, policy, and the summarization broker
that drafts a session note (ST-008 onward).

## What it never stores

**INV-7: no capture is ever persisted here.** There is no table for a frame, a transcript, an OCR string
or a note. `session_metrics` holds counts and durations and is shaped so there is nowhere to put one by
accident. A summarization request holds its bundle in memory for the length of one call and lets it go,
and `SummarizationPersistsNothingTests` counts every row in every table before and after a request to
prove it.

## Running it

It needs a Postgres and a signing key, and refuses to start without either. Both failures are loud at
startup rather than confusing at the first request.

```bash
docker run --rm -d -p 5432:5432 -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=screentail --name screentail-db postgres:17-alpine

export ConnectionStrings__Postgres='Host=localhost;Database=screentail;Username=postgres;Password=dev'
export Jwt__SigningKey="$(openssl rand -base64 48)"

dotnet tool install --global dotnet-ef --version 10.0.12
dotnet ef database update --project src/ScreenTail.Api --startup-project src/ScreenTail.Api
dotnet run --project src/ScreenTail.Api
```

Then `http://localhost:5000/health` answers without a token, and the OpenAPI document is at
`/swagger/v1/swagger.json`. Everything under `/v1` needs one.

**The signing key has no default on purpose.** A development default becomes a production key the first
time somebody forgets to set one, and nothing about the failure is visible: tokens sign and validate
perfectly, and anyone who has read the source can mint one for any tenant. Keys shorter than 32 bytes are
refused for the same reason.

## Tests

`dotnet test ScreenTail.Backend.sln` runs the whole API against SQLite over the same EF model, so every
query is compiled and executed on any machine with no database server. CI additionally applies the real
migrations to a real Postgres and checks every table arrived, because the migration SQL is the part
SQLite cannot check and is what a deployment actually runs.

## Drafting

The key has no default and none is in the repository. There are two places to put it.

**Developing locally — user secrets.** They live in your user profile, outside the repository, so there
is nothing here to commit by mistake and nothing to export each morning:

```bash
cd backend/src/ScreenTail.Api
dotnet user-secrets set "Summarization:ApiKey" "…"
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)"
dotnet user-secrets list          # prints the values, so mind the shoulder
```

A colon separates section from key here. The host reads user secrets only in the Development
environment, so nothing about a deployment changes.

**Deployed, or in CI — environment variables.** A double underscore is the separator:

```bash
export Summarization__ApiKey='…'
export Summarization__Provider=gemini-flash
export Summarization__DailyCostCapUsd=10  # per tenant, per UTC day
```

Without a key the service still starts and answers `501 not_configured`, which a technician can act on.
With one, a session gets exactly one model call, and:

- **The cap is checked before the call**, not after. A cap enforced afterwards is an alert.
- **A rejected draft is retried once, with the reasons.** A model that cited a frame it invented usually
  fixes it when told which one. Once, because a second failure is a bad day and a third is a bill.
- **Only an outage falls over to a fallback.** A rejected payload is rejected twice, and a revoked key is
  not fixed by spending money somewhere else.
- **Nothing is stored.** The bundle lives for one call; what is written is a `draft_costs` row holding a
  tenant, a session id, a provider name and a number.

`DraftValidator` refuses a draft before it is returned: an invented frame reference, a quotation nobody
uttered, a credential written back out, or a note that reads as an instruction. That last one is the
prompt-injection defence — text on a customer's screen can ask a model to emit a command, and a note is
where somebody downstream would find it and run it.

## Authentication

A device is activated once and holds a long-lived refresh token; the database keeps only its SHA-256. It
exchanges that for short-lived access tokens. Tokens are pinned to HS256 — a token that names its own
algorithm is a token that can name `none` — with issuer, audience, lifetime and signature all validated
and no clock skew allowed.

Revocation is checked on every request rather than at activation: a token stays valid for its whole
lifetime, so a technician who leaves has to stop working before it expires, not when it does.

A refusal never says which check failed. An error that explains itself is an error that helps whoever is
guessing.
