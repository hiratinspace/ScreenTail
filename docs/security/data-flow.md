# What ScreenTail does with your data

For an MSP evaluating ScreenTail, and for the customer who asks what that tool on the technician's screen
is. Written in plain language on purpose: a data-flow document nobody outside engineering can read is a
document that gets forwarded and never checked.

Everything here is enforced in code and named against the invariant that enforces it. Where something is
not yet built, it says so. **Revised 2026-09-22** against `main` at #132; not yet reviewed by a security
lead.

---

## The short version

ScreenTail watches a remote-support session on the **technician's** machine, turns it into a draft ticket
note, and shows that note to the technician before anything leaves the device. It never publishes on its
own.

```
  Technician's machine                                          Your PSA
  ┌─────────────────────────────────────────────────┐          ┌──────────┐
  │                                                 │          │          │
  │  screenshot ──► encrypted store ──► redaction   │          │ ticket   │
  │  (on click or   (unreadable       (masks what   │          │ note     │
  │   scene change)  until redacted)    it finds)   │          │          │
  │                                        │        │          └──────────┘
  │  microphone ───► transcript ───────────┤        │               ▲
  │  (technician only)                     │        │               │
  │                                        ▼        │               │
  │                                  draft request ─┼──► model ─────┘
  │                                        │        │    (or on-device)
  │                                        ▼        │         ▲
  │                                   YOU REVIEW ───┼─────────┘
  │                                   AND PUBLISH   │    nothing goes further
  │                                                 │    without this step
  └─────────────────────────────────────────────────┘
```

---

## Captured on the device

**Screenshots** are taken when the technician clicks, or when the screen changes enough to be worth
recording — not continuously, and not on a timer.

**Which windows** is restricted by default to the remote-support tool plus an allowlist of admin tools
(INV-5). "All windows" exists, is opt-in, and warns that it will capture other customers' data if the
technician has anything else open.

**Keystrokes are never recorded.** The keyboard hook produces categories and counts — "a burst of 14
characters", "a shortcut", "Enter" — and there is no field anywhere in the data format that can hold a
key or a character (INV-2). This is enforced by the schema as well as by the hook, so a future change
cannot quietly add one.

**Only the technician's microphone** is captured (INV-9). ScreenTail does not record the customer's audio
at all in v1. See [consent guidance](../legal/consent-guidance.md) for why that distinction matters.

**Capture stops by itself** when a password field has focus, when an excluded application is in front,
when a window is running as administrator, or when the screen looks sensitive. Those states **drop the
data rather than hiding it** (INV-6): no screenshot is taken and no typing event is recorded for that
interval. The gap is shown in the review screen so the technician can see that something was deliberately
not captured, rather than wondering what went missing.

**While any of this is happening, it is visible.** A pill on screen shows the state, always, including
while the technician is sharing their screen (INV-4). It cannot be dismissed mid-session, and it is kept
out of screen-share and recording software so the customer's view never shows it. There is no
silent-capture mode.

> **Status, 2026-09-22.** The UI is connected to the capture service (ST-085): the pill and a tray icon
> show live state. The UI tells the service every two seconds that the pill is on screen and where, and
> the service treats a pill it has not heard from in six seconds as absent — so a UI that has stopped
> painting without closing does not count as an indicator. **While no indicator is reported, the service
> suppresses capture** after a short grace for UI restarts, and writes the interval to the audit log; a
> UI that crashed, was quit, or was never started cannot leave the service recording. Between 2026-09-15 and 2026-09-16 this
> document said, correctly, that there was no indicator in the shipping build; an earlier version had
> claimed one that did not exist.

---

## Held on the device

The store is an **encrypted SQLite database** (SQLCipher) with a key held in Windows DPAPI under the
technician's own account.

**A screenshot never reaches the store unredacted.** A captured frame waits in a small in-memory queue,
the redaction worker takes it from there, and only the redacted, downscaled result is written
(ADR-0006). Every read path — the review screen, the bundle builder, export — still filters on frames
that have been redacted (INV-1), as a second line and for stores an older build wrote. A frame that
cannot be redacted, or that arrives when the queue is full, is **deleted and counted, not kept**: an
unreadable frame retained is an unredacted frame, and the count reaches the draft so it hedges rather
than claims to have seen everything.

Redaction runs on the device. It reads the text on each screenshot with the local OCR engine and masks
what matches — card numbers, national insurance and social security numbers, API keys and tokens,
password fields — and then, and only then, the frame becomes readable to the rest of the product.

**Raw data has a deadline.** Screenshots, transcript and timeline are deleted after a retention period
(default 7 days, configurable 1–30). The note survives; the material it was made from does not (INV-12).
"Delete everything" exists as a command the service accepts only with a typed confirmation it issued
moments before, and it removes tokens as well as data; the Settings screen that offers it is not built
yet (ST-081).

---

## What leaves the device

**Only when the technician presses Publish** (INV-3). There is no automatic publishing path in v1 — not
a scheduled one, not a "publish when confident" one.

For drafting, the note can be produced two ways:

- **On the device.** Local-only mode means **zero egress**, enforced by an allowlist that defaults closed
  and takes the destination's purpose from the calling code rather than guessing it from the URL (INV-8).
  A tenant can lock this on for every machine.
- **By a hosted model.** A redacted bundle is sent for one request. **The backend never writes frames to
  storage** (INV-7) — they are held in memory for the duration of the request and are not persisted, and
  the backend's tests assert that its storage does not grow.

**Logs contain no content** (INV-10). Not window titles, not OCR text, not transcript, not note text, not
company or ticket names. The record the diagnostics panel is built from has nowhere to put any of those,
which is a stronger guarantee than filtering them out. There is no metrics system yet, so "metrics" in
the invariant table describes an intent rather than a shipped component.

Since ST-011 (2026-09-25) that guarantee has a belt as well as a brace. Every process — service, UI,
backend — writes through one scrubbing log sink and no other: a value logged under a content-carrying
key (`Title`, `Text`, `Transcript`, `Note`, `Company`, `Ticket`, a secret) is written as `[redacted]`,
and every line and every exception passes through a path and address scrubber (`[path]`, `[email]`)
before it is written. A test scans every log template in the client for a placeholder that would carry
content. The level is `SCREENTAIL_LOG_LEVEL` on the client (`Logging:LogLevel:Default` on the backend)
and Information unless said otherwise.

**A crash leaves a stack trace and nothing else.** With `SCREENTAIL_CRASH_REPORTS=1` a crash writes the
exception types and frames — never the message, which is where a path or a title ends up — to
`%LOCALAPPDATA%\ScreenTail\crashes\`. Nothing reads that folder yet; sending is ST-098's. Without the
opt-in nothing is written.

**The tenant's policy is the one thing fetched in local-only mode** (ST-047). It is a handful of
settings — retention days, local-only and whether the admin locked it, capture scope — with no content,
fetched from the configured backend host under its own egress purpose, at start and hourly. It has to
be, because the policy that turns local-only on is the only thing that can turn it off. The last copy
that arrived is kept on disk and applies when the backend does not answer.

The diagnostics panel is filled from the service's own counters and policy over the pipe (ST-085); the UI
holds no value of its own to show, so the panel cannot say one thing while the service does another.

---

## What gets written down about what happened

Every session keeps an audit log: frames captured, frames deleted because they could not be redacted,
suppressed intervals and why, redactions by kind, bytes sent and to which host, and every deletion.

It is **hash-chained** — each row carries the hash of the one before it — so a row cannot be removed or
altered without breaking every row after it. That matters most for the rows recording that data was
*destroyed*, which are the ones somebody would want gone. The export says whether the chain verified, and
contains no content.

---

## What ScreenTail cannot protect you from

Said plainly, because a document that claims everything is covered is not one a security lead should
trust:

- **Anyone with the technician's Windows account.** They can read the store, because the technician can.
  The answer is your own account security and disk encryption, not ours.
- **Anyone running as SYSTEM** on that machine. Likewise, more so.
- **A compromised remote-support tool.** If the tool ScreenTail is watching is itself hostile, it controls
  the screen being captured. Worth saying aloud, since watching one is the product's entire premise.
- **A technician who opens the wrong window.** Scope defaults and suppression reduce this; they do not
  eliminate it. The review step exists partly for this reason — the technician sees every screenshot
  before it goes anywhere, and can exclude or blur any of them.

The full threat model, with mitigations mapped to tickets, is in [threat-model.md](threat-model.md).

---

## Not yet built

This document describes what is enforced today. These are named here rather than implied:

- **Publishing** (ST-077, ST-078) is not implemented. Nothing reaches a PSA yet.
- **The hosted backend** is built and runs locally on Postgres; it is not deployed (ST-007). INV-7 is
  asserted by its tests, which count every row in every table before and after a draft.
- **Enrolment** (ST-010) is not built. A Development-only flag issues a device token until it is.
- **Tenant policy sync** (ST-047) is not implemented, so "a tenant can lock local-only mode" describes the
  designed behaviour, not a shipped one.
- **The egress allowlist** (INV-8) **is in the path now**: every HTTP client the service builds goes
  through it, it refuses anything that is not HTTPS, and the list holds only the speech-model host and
  the backend address an operator configured. The UI process makes no HTTP calls.
- **"Delete everything"** is a command the service accepts behind a typed confirmation; nothing in the
  UI reaches it until the Settings screen exists (ST-081).
- **Audio capture is built** (ST-027). The service opens the technician's default microphone while a
  session is recording and nothing else; there is no code path for system or remote audio, so INV-9 is
  enforced by there being no other source to open.
- **On-device drafting is not built.** With no backend configured, nothing leaves the device and the
  session keeps its screenshots and transcript with a note that says it could not be drafted.
