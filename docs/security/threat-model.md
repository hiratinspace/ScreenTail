# Threat model — ScreenTail client (ST-012)

**Scope:** the Windows client — capture service, UI, local store, and the named pipe between them. The
backend has its own boundary (`backend/README.md`); it is built and runs locally, and is not deployed
(ST-007). Where a row depends on it, the ticket is named and the row says so.

**Revised 2026-09-22** against `main` at #132: T5, T8, T10, T12 and the wiring section below were brought
into line with the code (weaknesses P1-8). It has still not had a security-lead review.

**What an attacker wants.** Not the application. ScreenTail spends its day looking at *other people's*
screens — a dental practice's patient list, a law firm's matter files, an admin's password prompt — and
writes what it sees to disk on a laptop that travels. The prize is the capture, and the second prize is
making a technician believe capture is off while it is on.

**Assumed trusted:** the Windows kernel, the user's own account, and the machine's disk encryption. An
attacker with SYSTEM or with the user's own credentials has already won and nothing here pretends
otherwise. What this model addresses is another *unprivileged process on the same desktop*, a tampered
copy of our own binaries, and the network.

---

## STRIDE

| # | Threat | Category | What it gets | Mitigation | Where | Ticket |
|---|---|---|---|---|---|---|
| T1 | A process squats the pipe name before the service starts, and the UI hands it the session token | **S**poofing | Drives the UI: can report "idle" while capture runs (defeats INV-4 without touching capture) | UI verifies the pipe **server** is Authenticode-signed by our publisher *before writing the token*; connects with `TokenImpersonationLevel.Anonymous` so a rogue server cannot impersonate the technician | `IpcClient.ConnectAsync`, `WindowsServerVerifier` | **ST-012** |
| T2 | A process connects to the service's pipe and drives capture | **S**poofing / **E**levation | Start, stop, discard, mark — and the state the HUD shows | Per-user pipe ACL, per-run token, and the client executable must be signed by our publisher (ADR-0003, three checks) | `IpcServer`, `WindowsClientVerifier` | ST-004 |
| T3 | A modified copy of the service or UI binary, carrying the real certificate blob copied in | **T**ampering | Everything T1 and T2 get, past a check that looked like it would stop it | `WinVerifyTrust` hashes the file and walks the chain; a thumbprint read out of the certificate table proves only that bytes were copied | `Authenticode` | **ST-012** |
| T4 | Reading the store off a stolen or borrowed laptop | **I**nformation disclosure | Every frame, transcript and note held | SQLCipher with a DPAPI-protected per-user key; retention deletes raw data on a default 7-day clock (INV-12) | `SqliteSessionStore`, `RetentionJob` | ST-005, ST-044 |
| T5 | A frame reaches a reader before redaction has run | **I**nformation disclosure | An unredacted screenshot — a password prompt, a card number | A captured frame waits in a bounded in-memory queue and reaches the store only after redaction (ADR-0006); every read path still filters `redaction_pending`, for stores an older build wrote (INV-1). A frame that cannot be redacted, or that arrives when the queue is full, is deleted and counted, not kept | `PendingFrames`, `RedactionWorker`, `ISessionStore` | ST-005, ST-041, ADR-0006 |
| T6 | Keystrokes recorded alongside the screenshots | **I**nformation disclosure | The password being typed, not merely the field it went in | The hook emits categories and counts; no field anywhere can hold a key (INV-2), enforced by the schema's `additionalProperties: false` as well as the hook | Hook layer, `session.v1.json` | ST-024 |
| T7 | Text on a customer's screen steers the drafted note | **T**ampering | A URL, a PowerShell line, or "disable Defender" inside a note published to a ticket | The prompt states that OCR and transcript are evidence, never instruction; post-conditions refuse directives, invented quotations and credentials in **every** free-text field | `research/prompts/checks.py` | ST-061 |
| T8 | A local process reads the session token from memory or disk | **I**nformation disclosure | T2, without needing a signed binary | The token is generated per run and never logged. It is written to exactly one file, `%LOCALAPPDATA%\ScreenTail\ipc.token`, ACL'd to the user and moved into place so no reader sees it half-written; a process running as the same user can read it, which is the trust line stated at the top of this document | `IpcToken`, `IpcTokenFile` | ST-004 |
| T9 | Content leaks through logs or metrics | **I**nformation disclosure | Window titles, OCR text, note text — the same data, in a file nobody thinks of as sensitive | No content-carrying field exists in the diagnostics record or the metric shape (INV-10); the diagnostics panel and its clipboard text are built from that record alone | `Diagnostics`, `EgressGuard` | ST-071, ST-046 |
| T10 | Egress in local-only mode | **I**nformation disclosure | Anything, to anywhere | An allowlist `DelegatingHandler` that defaults closed and takes the purpose from the caller rather than inferring it from the URL (INV-8), installed at the service's composition root so every HTTP client the service builds goes through it; anything that is not HTTPS is refused whatever the list says | `EgressPolicy`, `EgressGuard`, `CaptureHost` | ST-046 |
| T11 | A technician denies a session happened, or a deletion is disputed | **R**epudiation | Plausible deniability over what was captured and what was destroyed | Hash-chained audit log that survives every deletion path, including discard and retention (INV-12); frame deletes and discards write rows inside the same transaction as the delete | `AuditChain`, `SqliteSessionStore` | ST-045, ST-075 |
| T12 | A second client instance, or a repeated failing client, floods the service | **D**enial of service | Capture stops for the session in progress | Any number of clients, but at most sixteen handshakes in flight and two seconds to say `hello`; when the table is full the oldest silent handshake is dropped so the real UI is never the one refused; rejections are rate-limited into the audit log rather than written per attempt | `IpcServer` | ST-004 |
| T13 | A listening socket on a technician's machine inside a customer network | **E**levation / **I**nformation disclosure | A second interface with no ACL, no token and no peer check | The client listens on nothing but the named pipe, and a test fails the build if a socket type appears in client source | `ReleaseSurfaceTests` | **ST-012** |
| T14 | A debug build path that is more permissive than the shipped one | **T**ampering | A check that is off in the build people actually run | No `#if DEBUG` / `#if TRACE` anywhere in client source, enforced by test | `ReleaseSurfaceTests` | **ST-012** |
| T15 | Admin policy changed locally to re-enable what a tenant forbade | **T**ampering | Cloud drafting where policy said local-only, for one machine | Policy is enforced client-side, not merely displayed (INV-11) | Policy sync | ST-047 |

---

## What is not mitigated, and why

- **An attacker running as the technician.** They can read the store's key through DPAPI, because the user
  can. The answer is the machine's own account security, not ours; pretending otherwise would put effort
  where it buys nothing.
- **An attacker running as SYSTEM.** Same, more so.
- **A malicious remote-support tool.** If the tool ScreenTail is watching is itself hostile, it controls
  the screen being captured. Out of scope, and worth saying aloud because the product's whole premise is
  watching one.
- **Screen content a technician is entitled to see but a customer would not want captured.** This is a
  policy and consent question, handled by scope defaults (INV-5), suppression (INV-6) and the review step
  (INV-3) — not by anything in this table.

## What is wired, and what is not yet

**T1's mitigation is wired.** `IpcClient.ConnectAsync` requires an `IServerVerifier` — required rather
than optional, so a `null` has to be typed on purpose — and the live shell passes `WindowsServerVerifier`
(ST-085). An unsigned development build falls back to the same-directory rule on both sides;
`scripts/windows/run-local.ps1` exists because `dotnet run` on each project separately fails that rule.

**T15 is not wired.** Policy sync (ST-047) is not built, so local-only mode today means "no backend
configured", which the guard enforces, rather than a tenant setting.

**The `netstat` evidence in ST-012's acceptance criteria** is a source-level guard here (T13, T14). A run
of the service with no listening ports is worth adding to the laptop's hardware checks once the service
runs there unattended.
