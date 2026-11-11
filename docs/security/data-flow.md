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

The OCR engine is given each frame at twice its captured size (2026-11-11), because it reads small
text badly and long random strings worst: on the hosted runner's probe, of 1,200 secrets drawn at 9
and 11 pt at 100% to 150% display scale, it masked 879 read as captured and 1,053 read at twice the
size. The larger reading is held to the pixels of a full HD frame at twice its size, so a larger frame
is read less enlarged (2560 by 1440 at 1.5 times), and one with that many pixels or more (4K) is read
as it is, as every frame was before. Its boxes are brought back to the frame's own pixels, rounded outward. A frame the engine
fails on is discarded, as before; there is one reading, not two.

Three rules match what the engine makes of a secret rather than the secret as written (2026-11-11),
because one character misread in a key's prefix or a password's cue and the exact rules never fired,
however well the rest was read:

- A GitHub, Stripe or npm key's prefix with its underscore read as a space, as nothing or as
  punctuation, and the l of "live" read as 1, I, | or i ("ghp 4WdRau…", "sk_1ive_pqg1…").
- Any run of text between spaces and separators (= : , ; and quotes) that looks random, whatever is
  before it: at least 16 letters and digits, with a capital, a small letter and a digit among them,
  changing between those three at no less than 0.4 of its neighbouring pairs. Random strings change at
  about 0.62; names in code built of words and a number ("Office365ProPlusRetail",
  "Int32.TryParse(value") at 0.35 or less, and are left alone. It takes 92% of random strings of 16
  characters and 99.5% of 36. A GUID, a hash in small letters, a hostname in capitals and digits, a path
  and a number grid are left alone. A key cut by a row's end is read whole by the wrap pass below, so
  a piece too short alone is taken with its continuation.
- "pass" with its colon misread ("pass. Juniper8749Quiver", "pass Tangerine3327Wobble"): only the
  very next word, and only when it has six characters or more with a digit or a symbol, so "pass the
  test" and "PASS FAIL" are left alone. It takes its rows as any password does.

What reading at twice the size costs is time and some over-masking. On the hosted runners a full HD
frame dense with text took 168 ms and 272 ms to read as captured on two runs, and 373 ms and 567 ms
at twice the size; a full HD settings dialog took 52 ms and 257 ms a frame for its whole trip through
the worker. Two readings, as captured and at twice the size, would cost the sum, 541 ms and 839 ms,
which the 700 ms a frame may take does not hold, so there is one. The budget is held on the spare
laptop (`RedactionThroughputTests`), not on a hosted runner. On pages with no secret, read at twice the
size, the existing key rule takes more prose: OCR runs a few words together, and a cue it finds inside
one ("account" in "accounting", "app" in "applied") takes the run with it, 3.9% of a prose page against
1.0% read as captured. Number grids and tables lose about a sixth more than before, and code pages
2.0% against 0.8%, the random-run rule's share of that a few names in code.

A match is masked by what is on the screen, not by how the OCR engine split it into words: every word
that touches it with no space between them goes too, so a value the engine read as two words is masked
whole. The space between two words is judged against the height of the row, or of the taller word
where the two sit on one line, so large text beside small is judged by its own size (2026-11-10). A password goes further: its cue's row and every row the match touches on the screen are
masked, the cue included, in the stored text and in the picture alike. The one exception is on the
cue's own row: the words wholly left of the cue stay readable, so the line still says what it was
(ScreenTail's cues are Latin, read left to right), unless any of what was masked on that row starts
left of the cue's end, and then the row is masked whole. A row with any right-to-left letter on it, Hebrew or
Arabic say, is masked whole. Nothing is inferred: what stays readable is chosen by where it is on the
screen, never by the order the OCR engine listed it. That is the owner's decision of 2026-11-09
(PR #268): more of a technician's line is masked than strictly needed, because a split the engine makes through a remote viewer's
scaling cannot be measured in advance, and a piece of a password left showing is a stored password.

A value that a terminal or a text box wrapped onto the next row is found by reading the page a second
time with each line's end joined to the start of each line that may continue it, and masking whatever
matches across a join (2026-11-10). What is joined is decided on the screen:

- A row is cut into runs where two words stand more than one row height apart, and at a word with no
  letter or digit in it (a divider between panes, a gutter's rule), so side-by-side panes and a
  line-number gutter are separate from the text beside them.
- A run's last token is joined to the first token of a run on one of the three rows below, no more
  than two row heights under it, that starts left of where the run above ends and ends no more than
  two row heights past it, since a line that overflowed is as wide as its box and its continuation
  cannot run past it.
- Every such run is tried, up to a bound: the two nearest on the row next below and the nearest one on
  each of the two rows after, taken from the six runs nearest on each row. That is four joins at most
  from one run's end, and eight where either row has a right-to-left letter, because those are joined
  the other way round as well.
- Each join is read twice where the two differ: with the pieces of a token run together, and with the
  page's own spaces, since a narrow real space reads as none and a rule that needs a word to end would
  miss it. It is read once more with a hyphen or backslash dropped from the end of the line above and
  a ">" or "+" from the start of the one below, where either is there.
- A token joined from above is joined on to what is below it, through a row with a right-to-left word
  too, up to 32 tokens in one chain.

The second reading only adds masks; a test holds the engine with it to masking everything the engine
without it masks, on 2,000 random pages. No edge of any box is known and nothing is inferred, so every
run's end over a run no wider is tried, and the cost is more masked than a page needs:

- A key, token or email that ends its row takes the first word of each run tried below it that
  completes its shape, the first word of the pane beside it included.
- A password that ends its row takes the whole of every such row in reach, the second and third down
  as well as the first, because a password takes every row its match touches. This is the largest
  cost.
- Numbers stacked one above the other that joined pass the card checksum are masked, however many
  rows of them, because a card number wrapped over four rows looks the same. On a grid of 1,200 random
  numbers, a cell a column: of four-digit numbers 518 are masked against 231 without this reading; of
  six-digit, 171 against 168; of eight-digit, 434 against none, two of them stacked being sixteen
  digits and each tried against four below it.

Not covered: a continuation that follows another word on its row after an ordinary space, with no gap
or divider before it; panes closer than one row height with no divider between; a continuation more
than three rows or two row heights down, or wider than the row above by more than two row heights; a
continuation that is not among the bound's nearest runs; a token the engine read as more than eight
pieces.

The reading costs time in proportion to the page: the text it gives the detectors is 1.3 times the
page's on ordinary prose, 4 to 8 times where every cell of a grid is a run of its own, and 15 times
where every row has a right-to-left word. Measured alone in the release build on a development Mac,
frames of 5,000 to 8,000 words built to cost most take 34 to 107 ms against 5 to 35 ms without it,
inside the 700 ms a frame may take. One shape is at risk: 5,000 email addresses, each a column of its
own, with the email rule on, takes 295 ms there (85 ms without) and 753 ms in a debug build. No test
holds a frame that long to the budget (the test holds 2,000 addresses), and it is not measured on the
target laptop.

The rules measure gaps against heights, so they do not depend on the display's scale, but boxes are
whole pixels: a token the engine split with a gap of a quarter of the row's height or less is joined
at 100%, 125% and 150% however its boxes round, while one between that and the cutoff of 0.35 is
within a pixel of rounding of it (`OcrScalingTests`). What the engine does with real text at those
scales, and through a remote viewer's resizing and JPEG compression, is measured by
`OcrScaledSecretTests` in the Windows-only suite, which reports how many secrets were masked whole,
part showing and missed at each scale and quality, and for each secret not masked whole which stage
lost it: the engine read no word on it, read it wrong, read it right and no rule matched, or a rule
matched and its mask stopped short. Before the frame was read at twice its size and the rules for
misreads were added (hosted run, 2026-11-11), of 1,200 drawn secrets at 100% to 150% display scale
879 were masked whole; of the rest 51 were not read, 264 misread (the underscore of a key's prefix in
165 of them, 1 and l and I confused in 168), 6 read right with no rule matching ("pass:" read as
"pass."), and none stopped short. After both, 1,165 of 1,200 (97.1%) were masked whole: 381, 392 and
392 of 400 at 100%, 125% and 150%. What is left is the engine's: 25 secrets with no word read on them,
mostly the first row of a wrapped key, and 10 misread with characters dropped ("--key BeF2" for
"sk_live_Jtb1o1BeF2"). Through a viewer that resizes and compresses, 1,250 of 2,400 secrets were
masked whole across every scale and quality before, and 1,986 after; at 0.6 still under half, at
0.9 and above 94% or more. The masks of a reading as captured and one at twice the size together
would mask 1,183 of 1,200 (98.6%), but two readings cost more than the frame may take (below). The
test holds each kind of secret, at each scale and quality, to what was
measured less a tenth, and part showing to what was measured and two more. These are synthetic pages,
not the labelled corpus ST-042's recall gate needs.

Not covered either is a mirrored layout with a Latin label
and no right-to-left letter, such as "hunter2 Password:", where the pattern takes a word on the next
line as the value: the value left of the cue is kept, because the row rule assumes a Latin cue reads
left to right. Transcripts are scrubbed by the words alone.

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
