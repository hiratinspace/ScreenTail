# What ScreenTail does with your data

For an MSP evaluating ScreenTail, and for the customer who asks what that tool on the technician's screen
is. Written in plain language on purpose: a data-flow document nobody outside engineering can read is a
document that gets forwarded and never checked.

Everything here is enforced in code and named against the invariant that enforces it. Where something is
not yet built, it says so. **Revised 2026-09-28** against `main` after #168; not yet reviewed by a security
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
technician has anything else open. The Windows desktop, taskbar and Task View are never captured, in
either mode: the desktop's rectangle is every monitor, so a picture of it would be a picture of every
window in front of it. Neither is a shell window whose type cannot be read. File Explorer counts as an
admin tool only in a folder window.

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

**Speech is dropped on the same terms.** What the technician says is kept only while the window in front
could be photographed. While an excluded application, an elevated window or any other window outside the
session's scope is in front, the microphone's audio is thrown away as it arrives: it is never transcribed
and never stored, so a credential read aloud off a password manager does not reach the transcript. A
sentence already under way when such a window comes to the front is dropped with it. *(Until 2026-10-14
speech was gated on the session state alone, and was transcribed and stored with a password manager in
front.)* For an excluded or out-of-scope window the pill still reads Recording, because the session is
still recording clicks; the "What's being captured right now?" panel names the window's scope.

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
moments before, and it removes tokens as well as data: everything under the data directory except the
downloaded speech models (and the program's own folder, if it was installed there), so the device has to
be activated again; Settings → Privacy & Redaction offers it (ST-081), and exports the audit log with its
verification beside it.

---

## What leaves the device

**Only when the technician presses Publish** (INV-3). There is no automatic publishing path in v1 — not
a scheduled one, not a "publish when confident" one. One press is one request: a refused publish is not
sent again to learn why it was refused, and once anything has landed Publish is refused, so Retry (which
sends only what failed) is the one way on.

**Not every screenshot can go.** One that looked like a sign-in screen, or was taken while capture was
paused or suppressed, stays on the device. Review shows it marked "withheld" and it cannot be switched
on. At publish the service attaches only frames of that session that its own store says are included and
not withheld, whatever the request names, so the drafting bundle and the ticket follow the same rule
(ST-078).

For drafting, the note can be produced two ways:

- **On the device.** Local-only mode means **zero egress**, enforced by an allowlist that defaults closed
  and takes the destination's purpose from the calling code rather than guessing it from the URL (INV-8).
  A tenant can lock this on for every machine.
- **By a hosted model.** A redacted bundle is sent for one request. **The backend never writes frames to
  storage** (INV-7) — they are held in memory for the duration of the request and are not persisted, and
  the backend's tests assert that its storage does not grow. The model is the one the deployment's
  `Summarization:Provider` names, and no other: Anthropic's Claude (`api.anthropic.com`, under the
  operator's Anthropic account) when it is `claude`, Google's Gemini Flash when it is `gemini-flash`, the
  default. What either receives is the screenshots after redaction, the masked text read from them and the
  masked transcript (INV-1), inline in the request, plus a second request carrying the reasons when the
  first answer breaks a rule. On Anthropic, a session its safety checks decline may be re-run on a second
  Anthropic model in the same call; Anthropic then keeps a content-hash routing record (not the content)
  for about an hour, except for zero-data-retention organizations.

**Logs contain no content** (INV-10). Not window titles, not OCR text, not transcript, not note text, not
company or ticket names. The record the diagnostics panel is built from has nowhere to put any of those,
which is a stronger guarantee than filtering them out. The metrics system (ST-098) is built the same
way: `shared/contracts/session-metric.v1.json` is the whole field list — the session id, counts, times,
the edit ratio and whether it was published — and a test on each side fails if the type ever grows a
string field other than the id. Nothing is sent unless telemetry is on.

Since ST-011 (2026-09-25) that guarantee has a belt as well as a brace. Every process — service, UI,
backend — writes through one scrubbing log sink and no other: a value logged under a content-carrying
key (`Title`, `Text`, `Transcript`, `Note`, `Company`, `Ticket`, a secret) is written as `[redacted]`,
and every line and every exception passes through a path and address scrubber (`[path]`, `[email]`)
before it is written. A test scans every log template in the client for a placeholder that would carry
content. On the backend the framework's own lines count as well: the request line is logged without its
path or query string (a ticket search's typed text, a company in a route), a call to a tenant's PSA
without its address (the tenant's site, a ticket id), and the HTTPS redirect without the address it
sends a request to. A request Kestrel cannot parse is logged with its connection id and the status it
was refused with, never the request line or header it quotes, and the message of a bad-request
exception is not written wherever it is logged. The backend builds each line from its template and
writes `[redacted]` in place of a sensitive value, so the value is never written, however it overlaps
another or is formatted. A line with no template has each value cut out of it, and is withheld whole
when that cannot be done cleanly. Tests drive real requests through the API at Information, Debug and
Trace, in the Development pipeline and in the Production one (which adds the HTTPS redirect), send
malformed requests to Kestrel itself at Debug and Trace, and fail if any of that text reaches the log.
Another fails if a second log sink is added beside the scrubbing one in either environment. The level
is `SCREENTAIL_LOG_LEVEL` on the client (`Logging:LogLevel:Default` on the backend) and Information
unless said otherwise.

**A crash leaves a stack trace and nothing else.** With `SCREENTAIL_CRASH_REPORTS=1` a crash writes the
exception types and frames — never the message, which is where a path or a title ends up — to
`%LOCALAPPDATA%\ScreenTail\crashes\`. Nothing reads that folder yet; sending is ST-098's. Without the
opt-in nothing is written.

**A local-only session is drafted on the device** (ST-065 AC3). When the egress guard would refuse the
drafting request (local-only mode, or no backend at all), nothing is queued and the note is written
here from the transcript and the screenshots: one step per thing said, low confidence, marked local.
No model runs, so no model is contacted; the note says what it is.

**What the technician changes about drafts is learned as signals, not text** (ST-067). At publish the
service compares the draft as written with the draft as published and keeps only structural ids —
`past_tense`, `shorter_steps`, `no_full_stops`, `sentence_case`, `fewer_steps` — for the last ten
sessions, in `style.json` beside the store. The next drafting request carries the ids a technician has
earned; the backend turns the ones it knows into sentences of its own and refuses any other id, so
nothing a client sends can become a line of the prompt.

**The tenant's policy is the one thing fetched in local-only mode** (ST-047). It is a handful of
settings — retention days, local-only and whether the admin locked it, capture scope — with no content,
fetched from the configured backend host under its own egress purpose, at start and hourly. It has to
be, because the policy that turns local-only on is the only thing that can turn it off. The last copy
that arrived is kept on disk and applies when the backend does not answer. The speech model is not
fetched in local-only mode either (ST-046): it carries no customer data, but it is a request the
technician did not make. A machine that has no model when local-only is turned on records sessions
without narration until the model is present or the policy is lifted, and the recogniser says so.

The diagnostics panel is filled from the service's own counters and policy over the pipe (ST-085); the UI
holds no value of its own to show, so the panel cannot say one thing while the service does another.

---

## What gets written down about what happened

Every session keeps an audit log: frames captured, frames lost because they could not be redacted,
suppressed intervals and why, redactions by kind, bytes sent and to which host, and every deletion. A
frame turned away before it was captured, because redaction had fallen behind, is counted as lost and
not as captured, so the lost count can be larger than the captured one.

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
  before it goes anywhere, and can exclude or blur any of them. A blur deletes the text read from that
  screenshot as well as the pixels.

The full threat model, with mitigations mapped to tickets, is in [threat-model.md](threat-model.md).

---

## Not yet built

This document describes what is enforced today. These are named here rather than implied:

- **Publishing is built and untested against a real PSA.** A note, a time entry and a knowledge-base
  article go through the service to the backend, which holds the tenant's credentials in its vault; the
  client never learns which PSA. The backend calls a tenant's provider only at a public internet
  address: a site on its own machine or a private network is refused when stored and again when the
  call connects. The ConnectWise sandbox check and a Hudu key are the owner's.
- **The hosted backend** is built and runs locally on Postgres; it is not deployed (ST-007; `deploy/` is ready for a host, with TLS at the proxy and the database on an internal network). INV-7 is
  asserted by its tests, which count every row in every table before and after a draft.
- **Enrolment is built on both sides** (ST-010): an invite code, issued by the operator, activates the
  device; the service keeps the refresh token under DPAPI and renews access tokens; Settings → This
  device takes the code. Owner sign-up and SSO, and the invite email, wait for the web admin and a mail
  provider.
- **Tenant policy sync is built** (ST-047; see the questionnaire's 3.4). Exclusions, redaction patterns
  and telemetry are not yet fields of the policy.
- **The egress allowlist** (INV-8) is in the path: every HTTP client the service builds goes through it,
  it refuses anything that is not HTTPS, and the list holds only the speech-model host and the backend
  address an operator configured. The UI process makes no HTTP calls.
- **"Delete everything"** is offered in Settings → Privacy & Redaction behind the service's typed
  confirmation (ST-081).
- **Audio capture is built** (ST-027). The service opens the technician's microphone while a session is
  recording, and otherwise only for Settings' level meter (below); there is no code path for system or
  remote audio, so INV-9 is enforced by there being no other source to open. The microphone is the one
  chosen in Settings, Capture when it is connected and Windows' default communications device otherwise
  (ST-080 AC3); a chosen device that is not connected never stops a session. The choice is read each
  time the microphone opens, so within a session it can apply after a pause or a moment of
  suppression. The service's log says only how the choice went (chosen, default, fallback, none),
  never a device's name. Each stretch of speech is stamped with the session it
  was heard in and is written into that session or nowhere, so a segment the model finishes after one
  session ends cannot land in the next customer's transcript; discarding a session drops what was still
  queued for it. Stop waits, within the same grace as the last screenshots, for speech still being
  transcribed, so the note includes the last things said. Nothing said during a pause or a
  suppression is heard. A sentence said while recording and finished by the model after a pause or a
  suppression began is still written (scrubbed like any other), as a screenshot taken before a pause
  is redacted and written during it. A sentence still being said when the session pauses or stops is
  cut at that moment and kept the same way, and Stop waits for it; nothing heard after the moment is
  joined on to it. One still being said when capture is suppressed (a password field, a sign-in
  screen, no indicator on screen) or when a window outside the scope comes to the front is dropped,
  never kept, even when the session stops at the same moment or the suppression is over before the
  next piece of audio arrives. Speech with no pause in it that runs past the longest segment (20
  seconds) is held back from the model in the meantime, so the part already cut is dropped with it;
  only parts a whole longest segment older go ahead. The speech model is released after five minutes with nothing to
  transcribe and loaded again by the next sentence.
- **The level meter in Settings, Capture opens the microphone outside a session** (ST-080 AC3, 2026-10-31),
  and only this way: while the microphone card is on screen, scrolled into view, with the window in
  front and not minimised, and the desktop unlocked and awake (Win+L, a user switch, a Remote Desktop
  disconnect and sleep each close it). The UI asks for the level about ten times a second, each request
  after a round trip through its own UI thread; each request keeps the device open for a moment, so it
  closes by itself within a few seconds when the card is hidden, the window goes behind another, or the
  UI hangs or stops. It is open five minutes at most at a stretch, then stays closed until the card is
  put away and looked at again (a session in between does not reset it). A window brought to the
  front never counts as the technician being back at a locked desktop; only an unlock or a resume does. It never opens while a session is recording, paused, suppressed or
  finishing, and a session that starts closes it; the pill and the tray are not involved, because
  nothing is captured. It opens the device as plain capture, not as a call, so Windows does not turn
  other sound down. Each frame of audio becomes one number and is dropped: nothing heard is kept, transcribed,
  written, sent or logged, and the meter has no field that could hold a sample. Windows' own
  microphone-in-use indicator lights while it runs, which the technician sees beside the card.
- **The on-device model is not built** (ST-065). A local-only session is drafted on the device from
  its transcript and screenshots instead — a plainer note, marked local — so nothing leaves and
  nothing is lost.
- **Binaries are not signed** (ST-112). The peer check falls back to the same-directory rule on
  development builds, and the spare laptop's Application Control policy has blocked fresh service
  builds more than once.
