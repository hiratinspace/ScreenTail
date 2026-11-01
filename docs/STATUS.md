# ScreenTail: project standing

**Snapshot taken:** 2026-10-29, after the day's merges (#225 to #234): the loop harness, Claude as a
drafting provider, the modern Windows 11 look, auto-update (switched off), the processor limit and the
demo's diagnostic report. Updated 2026-10-31 for the web admin (ST-099, branch `st-099-web-admin`)
and 2026-11-01 for its hardening (branch `st-099-hardening`) and for invite-code guessing (branch
`st-010-activation-guessing`)
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next.
**Source of truth:** the **Status** line on each ticket in `Build Plan/02-Backlog-v0.4.md`. This page summarises; the backlog decides. `docs/README.md` says which document is which.

---

## 1. Where things stand

**Everything between a real session and a published note is built, merged and green. None of it has run
end to end on a real machine.** Capture, redaction, speech, the bundle, the outbox, the sender, the
backend endpoint, the model call, Review and the publish path each have tests, and the capture-path tests
run on the spare laptop on every pull request that touches them. The whole chain has never been exercised
with a real session, a real backend, a real key and a person reading the note, all at the same time.
Built is not proven. That first run is **M1**, and it is the next thing to happen.

**The loop has run end to end on a Windows VM, unattended (#230, ST-013).** `scripts/windows/e2e/`
runs the backend, a development device, the capture service, the UI and one session through to the note
on the demo PSA's ticket, with a scripted drafting provider in place of the model, and says PASS or FAIL
for each step (`docs/dev/e2e.md`). CI runs it on every pull request that touches the client, the
backend or the scripts, report-only for now. Its first runs found the likeliest reason the demo "was
not working properly": every session with a backend ended on "draft failed: no summarization provider
is configured" and never reached "Draft ready", although its draft arrived. That is fixed: a session
whose draft is on its way now rests in a state of its own, "Not recording: drafting the note", until it
arrives. Two smaller finds were fixed with it. The laptop's run with real hotkeys and clicks has not
passed yet. It does not replace M1: the model's note and a person reading it are still owed.

**M1 is two commands now.** `scripts/windows/demo/` (#219) sets up, starts and stops the whole loop on
the laptop: the development certificate, the drafting key, the signing key, PostgreSQL, the backend, a
development device token, the capture service and the UI, each checked and each failure said in plain
words. `docs/dev/demo.md` is the guide, with a demo story and a rehearsal checklist of what has never
been seen on a real machine. The manual step list, `docs/dev/first-draft-end-to-end.md`, does the same
thing by hand. When something goes wrong, `report.cmd` (#227) writes one plain-text file to send: what
looks wrong first, then each part's state and the last lines of each log, with secrets, email addresses
and user paths removed. The scripts themselves have not yet been run end to end by a person.

**Claude can draft instead of Gemini** (#225, ST-063). `Summarization:Provider=claude` selects
Anthropic's `claude-opus-5-5`; the backend's default is still Gemini, and the demo's setup asks which
one, with Claude as its default. It is built and tested against recorded answers, and has not yet been
measured against the real model.

**A demo PSA stands in for ConnectWise in Development** (#218, `docs/dev/demo-psa.md`), so a
demonstration can end with the note on a ticket while there is no ConnectWise sandbox. It is named
"Demo PSA (not ConnectWise)" wherever it shows, the backend refuses to start with it outside
Development, and it proves the publish path, not ConnectWise. Separately, the backend tests now run the
real ConnectWise and Hudu providers over HTTPS against stand-in servers that check the published shapes
(#226). That proves our side as we read the vendors' documentation, not that the vendors accept it.

**The client has a modern Windows 11 look** (#228, ST-016): new tokens (v0.5.0), every control and
screen restyled, no change in behaviour. The blue accent and the text wordmark are placeholders until
the owner gives a brand colour and logo, and the spec amendment waits for the owner's acceptance.

**Auto-update is built and switched off** (#233, ST-113, ADR-0008 Proposed). The client believes only a
manifest signed with a key compiled into it, downloads only while nothing is recording, and offers
Install only when idle. No build makes an update request until the owner creates the signing key, the
hosting and the release environment.

**The web admin is built** (2026-10-31, ST-099, ADR-0009 Proposed). Owners sign in with Google, as the
owner chose; the backend checks Google's ID token itself and keeps the session in an HttpOnly cookie with
a CSRF header, apart from device tokens in every direction. An address on the operator's signup list
creates its tenant on the first sign-in; the owner then invites technicians (the code shown once), sees
seats and devices activate, revokes a device, sets the policy and sees integration status. Every page
scored 100 for accessibility in Lighthouse. It is off until a Google OAuth client id is set, and has not
yet been signed into with a real Google account (`docs/dev/web-admin.md`). **Hardened 2026-11-01:** the
anonymous sign-in and activation routes are rate limited per address (sign-out never), every Google
token must carry a single-use nonce the page asked for, and the API and the page send a
Content-Security-Policy and related headers (ADR-0009 "Hardening"). **Invite codes are 99 bits since 2026-11-01:** twenty characters
in four groups, so guessing from any number of addresses is hopeless without a lockout anybody could
abuse; refused codes across all addresses log a content-free warning (ADR-0009 "Guessing invite codes").

**ST-031's processor limit is built** (#232). Ten seconds over the recording budget and capture gives up
one step at a time: fewer scene samples, then one OCR reader, then scene sampling stops. The input hooks,
click screenshots, speech and redaction are never throttled. It is proved on a fake clock; seeing it
trip on the laptop is an owner step (`docs/dev/performance.md`).

**The 2026-10-12 review (efficiency, security and regression tests) is worked through.** Its 42
pre-pilot findings landed as #173 to #195 by 2026-10-23. The backend P2 findings landed as #199 to #202
and #206. The client P2 findings landed in nine groups between 2026-10-25 and 2026-10-27: #207 (pipe),
#208 (sessions, input, CI), #210 (review and publish), #211 (shell and HUD), #212 (speech), #213 (store
and host), #214 (redaction), #215 (settings and network) and #216 (capture and detection). Each was
written test first with a sabotage check and read by an adversarial reviewer before it got a pull
request, and eight of the nine waited for the laptop's hardware jobs. The fixes that crossed groups
landed together as #220. The findings themselves stay in an untracked local file. What the run left
for the owner to decide is in §5.

**Of 85 tickets, 67 are Done or built, 8 are Partial and 10 are Open** (counted from the Status lines on
2026-10-31). ST-113 moved from Open to Partial on 2026-10-29; ST-099 from Open to built on 2026-10-31.

- **45 are done outright.**
- **22 are built with a named part still owed:** ST-099 (the web admin) owes a run with a real Google
  account and the owner's OAuth client id, and its proxy listed once hosting exists; ST-091, ST-092, ST-095 and ST-096 are done against
  recorded shapes and owe the real ConnectWise and Hudu checks; ST-093, ST-094 and ST-097 are done on the
  server side; ST-073, ST-076, ST-077, ST-078 and ST-085 are done on the Mac side; ST-080, ST-081, ST-082
  and ST-083 are built and not yet seen on a real screen; ST-010 (the device side), ST-062 (the harness;
  the corpus is owed), ST-065 (the on-device draft; the local model is owed), ST-084 (the static half)
  and ST-115 (round one) each owe their second half.
- **8 are Partial,** and each says what it waits for: ST-013 (the loop harness runs in CI; a passing
  laptop run with real clicks, the golden replay, resource figures and the nightly schedule are owed),
  ST-014 (two technician sessions), ST-027 (the WER number needs a human recording), ST-031 (measured and
  enforced on the laptop, and the throttle built; the golden replay, the throttle seen on the laptop and
  the nightly harness are owed), ST-042 (the recall gate needs ST-030's labelled frames), ST-112 (the
  installer is built and smoke-tested unsigned in CI; signing needs the certificate), ST-113 (auto-update
  is built and tested; switched off until the owner creates the key, the hosting and the release
  environment), ST-114 (the security-lead review).
- **10 are Open:** ST-007 (hosting), ST-017 (the usability round), ST-030 (golden sessions), ST-110,
  ST-111 and ST-116 (the pilot), and the four v1.1 and v1.2 tickets ST-120 to ST-123.

Phases A and B of the ordered plan (backlog Part C) are complete apart from what needs the owner: ST-030
(golden sessions; a ScreenConnect trial and a second machine) and ST-062's corpus, which is ST-030's.

## 2. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (**public**) | https://github.com/hiratinspace/ScreenTail | `main` green at #227; one open pull request, #235 (repository housekeeping) |
| Build plan | `Build Plan/` | Spec at **v0.4.3** accepted; the v0.5.0 amendment (the modern look, #228) waits for the owner. Backlog Part C is the ordered plan, Part D the amendments |
| Client | `client/` | `.Shared` (schema and IPC), `.Core` (platform-neutral logic), `.Platform` (Windows adapters, ADR-0005), `.Service`, `.UI`. **2,759 tests** in the suite that runs anywhere and **287** in the Windows-only suite (CI on #229, ten of them skipped on the hosted runner by design) |
| Backend | `backend/` | ASP.NET Core API: tenants, devices, device tokens, invites, policy, the summarize and publish endpoints, the Gemini and Claude providers, the cost ledger, the credential vault, ConnectWise and Hudu clients, the demo PSA, the update policy fields, and owner sign-in with Google and the web admin's routes (ST-099), with their rate limit, sign-in nonce and security headers. **1,385 tests** (two skipped). Runs locally on PostgreSQL; not deployed (ST-007) |
| Research | `research/` | Fixtures, note prompt v1 with its hardening cases, the shared validator rules, the WER script and the draft-quality harness. **255 tests** |
| Web admin | `web/`, `docs/dev/web-admin.md` | React and TypeScript on the shared tokens: Google sign-in, first-run tenant, Technicians and seats, Policy, Integrations, Download client (ST-099), with its security headers in `web/security-headers.ts`. **40 component, client and build tests**; Lighthouse accessibility 100 |
| Shared contracts | `shared/` | The session schema, the design tokens, and the wire contracts (summarize, publish, session metric, tenant policy) both sides test against |
| Demo setup | `scripts/windows/demo/`, `docs/dev/demo.md` | Setup, start, stop and the diagnostic report for the whole loop on the laptop (#219, #227). Parse-checked in CI; not yet run end to end by a person |
| Loop harness | `scripts/windows/e2e/`, `docs/dev/e2e.md` | One session through the whole loop with a scripted drafting provider (#230). Green on the hosted VM, report-only in CI |
| Installer and updates | `installer/`, `docs/dev/installer.md` | The MSI and setup program, smoke-tested unsigned in CI (#224), and the signed update channel (#233). Signing and updates stay off until the owner sets them up |
| Windows test loop | Hosted Windows VM and the spare laptop `SCREENTRAIL` | Both run on every PR that touches capture. The laptop's jobs were green on #214, #218, #219 and #220 on 2026-10-27. Since #229 a laptop job fails at once, and says so, while the demo is running |
| Branch protection | Ruleset "main" | **On.** PR required, `ci-ok` must be green, no force-push, no deletion. The repository is public; three GitHub settings keep strangers' code off the laptop (`docs/dev/windows-test-loop.md`, Security) |

## 3. What the code does today

- The capture service starts per user, serves an authenticated named pipe (user ACL, verified client
  executable, per-run token in a file only that user can read), recovers what a crash left behind, and
  runs retention hourly with a vacuum only when a third of the file is free.
- **The UI is connected** (ST-085). A tray icon and the recording pill show live state and say what is
  holding capture when it is paused (#211); the diagnostics window is filled by the service; discard and
  erase need a typed confirmation the service issued moments before. **Review, History, Settings and
  the onboarding wizard are real panes** fed over the pipe. The UI tells the service every two seconds
  that the pill is on screen and where; the service treats a pill it has not heard from in six seconds
  as absent and suppresses capture until one is back (INV-4). Every start, automatic or over the pipe,
  waits for a current indicator report and takes the same session policy (#220). Once a session runs,
  only a report with the pill's real size counts: a pill put away before the start has a short grace to
  come back, and then capture is suppressed (#231).
- The session state machine owns the lifecycle. Sources can only write while recording (INV-6). The
  input hooks are installed while a session exists and removed when it ends. Auto-start follows the
  remote tool in front, and the auto-stop grace runs from when the tool loses focus (#216).
- **A captured frame never touches the disk unredacted.** It waits in a bounded in-memory queue (depth
  4), the redaction worker takes it from there, and only the redacted, downscaled result is stored
  (ADR-0006). A frame the OCR engine cannot read, or that arrives when the queue is full, is dropped and
  counted, and the count reaches the draft so it hedges (ADR-0004) and the diagnostics window (#220).
  `NothingUnredactedIsEverInTheStore` reads the bytes back to prove it.
- Redaction finds SSNs, Luhn-valid cards, credential shapes, spoken passwords in the transcript and
  tenant patterns; overlapping regions are merged before masking. Since #214 the spoken-password walk
  masks at least as much as it did before, held by a frozen copy of the old walk.
- **Speech works.** The microphone opens with the session; Whisper `base.en` runs on two threads and is
  loaded when the first session wants it; the model download is hash-checked and cannot fill the disk; a
  voice-activity gate and a hallucination filter sit in front of the transcript. What is said while
  capture is suppressed is dropped, and the last sentence before Stop is waited for (#212).
- **The microphone is chosen in Settings, Capture, with a live level** (ST-080 AC3, IPC contract 7). The
  choice is kept by Windows' id in `capture.json` and used the next time the microphone opens; one that
  is not connected falls back to Windows' default and the card says so. The level is read only while the
  card is on screen with the window in front and the desktop unlocked, through a short lease the service
  ends by itself, five minutes at most at a stretch, never during a session, and nothing heard is kept
  or sent.
- **The drafting path is wired end to end.** Bundle, outbox, sender, `POST v1/sessions/summarize` with a
  device token, Gemini Flash, the validator (invented frames, fake quotations, credentials,
  instruction-shaped text; the backend's and the research copy share one rule set since #206), and the
  note in the store. One model call per session; the daily cap is checked before the call. Nothing is
  sent before the first policy fetch has landed (#213). A retryable outbox item is failed only after a
  day and eight attempts (#215). A local-only session is drafted on the device instead (#168). Claude can
  be chosen in place of Gemini (#225). A session whose draft is on its way says "drafting the note"
  rather than failed (#230).
- **The publish path is built end to end**: note, time entry and KB article from the Review pane, with
  the company mapping prompt (ST-097), the ticket pre-selected from the window (ST-077) and recent tickets
  on focus. A provider that asks to wait says for how long, and a Hudu company that was deleted asks
  for another (#229). None of it has met a real PSA; in Development the demo PSA can stand in (#218), and
  the tests drive the real providers against stand-in servers (#226).
- **A recording session watches its own processor use** (ST-031 AC2, #232) and, over budget for ten
  seconds, samples the screen less, then reads one frame at a time, then stops scene sampling. Every frame
  it does take is redacted exactly as before (INV-1).
- **Updates are built, and off** (ST-113, #233): a signed manifest checked against a compiled-in key, a
  download only while nothing records, Install offered only when idle, the channel chosen in Settings
  unless the admin locks it. The trust file is empty, so no build checks for updates yet.
- Every HTTP call the service makes goes through the egress allowlist at the composition root. Anything
  that is not HTTPS, or not the model host or the configured backend host, is refused (INV-8).
- The audit log is hash-chained with a head anchor, so truncation from either end is detected. A draft,
  a publish and a metric each write a `bundle_sent` row with the host and the size, never the body (#220).
- Every process logs through one scrubbing sink (ST-011): content-carrying values are `[redacted]`,
  paths `[path]`; the backend's scrubber knows every name the client's does (#220). A crash leaves a
  stack-only report, and only when opted in.
- Design tokens generate WPF dictionaries and web CSS from one source; the gallery renders in CI in
  dark, light and high contrast. Since #228 they give the client a Windows 11 look (tokens v0.5.0).

**Not built:** the local text model (ST-065); hosting (ST-007); mail for invites (the operator's CLI and
the web admin show the code once, to hand on). **Built and switched off until the owner sets them up:** the
installer's signature (ST-112, ADR-0007), auto-update (ST-113, ADR-0008) and owner sign-in to the web admin
(ST-099, ADR-0009: off until a Google OAuth client id is set).

### Seen on real hardware, and not

**Seen:** the Windows-only suite and the input-injection job run on the laptop for every pull request
that touches capture, so hooks, foreground detection, screen capture, OCR, the pipe's handshake and the
latency and performance budgets are checked on a real desktop, not only on a cloud VM. ST-001's spike was
measured there. The drafting call was measured against the real Gemini model on 2026-09-19 (ST-063), on
the heaviest bundle a client may send, not on a recorded session.

**Not seen yet:**

- The full loop: a real session drafted by the real model and read in Review. That is M1.
- The demo scripts run end to end by a person, and the loop harness with real clicks on the laptop.
- A Claude draft from the real model, with its time and cost on a heavy session.
- The processor limit tripping on a real overload (the laptop step in `docs/dev/performance.md`).
- The tray and pill following a real session with the service up, and screenshots over a remote window
  (ST-085's own walk-through).
- The microphone picker and its level meter with a real headset, plugged and unplugged, and Win+L with
  the card open turning Windows' microphone light off (ST-080 AC3). The tests drive the lock, sleep and
  activation rules directly; Windows' real delivery of those events (Win+L, sleep) is checked only by hand.
- Review, History, Settings and the onboarding wizard on a real screen, in the new look. CI renders them
  over fixtures in three themes; dropdowns, tool tips, hover and pressed states are not rendered there.
- A publish to ConnectWise or Hudu. Both clients are tested against recorded shapes and, since #226,
  stand-in servers only.
- The speech model's word error rate on a real narration (ST-027), and redaction recall on real captures
  (ST-042).
- RDP opacity from a second machine (ST-001 AC2), the Narrator and keyboard pass (ST-084), the toasts on
  Windows (ST-073), an installed, signed build, and an update installed through Windows' prompt.

## 4. Decisions already made

- **.NET 10 LTS** (support for 8 ends November 2026).
- **OCR on the full-size frame**; only the display and upload copy is shrunk (ADR-0001 finding 2a).
- **Spec v0.4.1 (Q1 to Q6):** HUD always visible during screen-share; Internal note type default; tray
  discard stops capture before confirming; Publish disabled after a failed draft; elevated-window
  wording; bulk discard confirms by count.
- **Spec v0.4.2:** `text.muted` and light `accent.primary` adjusted, `text.on-accent` added, and state
  colours are indicators, never body-size text.
- **Spec v0.4.3:** three S3 wording changes from ST-015 and ST-074. Accepted 2026-09-15 (Part D, D-6).
- **A draft reports unrounded active minutes**; the client applies the tenant's billing rounding.
- **2026-09-15 sequencing** (Part D, D-1 to D-3): ST-060 waited for ST-042's engine, not its recall
  gate; the backend runs on PostgreSQL locally; the usability round gates the pilot, not the publish
  panel.
- **Model provider: Gemini Flash** (`gemini-3.6-flash`, low media resolution), chosen and measured
  2026-09-19, and still the backend's default. **Claude** (`claude-opus-5-5`) was added as the other
  provider on 2026-10-29 (#225), and the demo's setup offers it first. OpenAI stays swappable behind the
  same interface.
- **2026-09-22, four decisions from the efficiency review:** US only for now (no regional model
  endpoint); Whisper `base.en` on every machine, two threads; the microphone follows the session, not
  the recording state; Gemini billing is the owner's to enable and gates *measurement*, not M1.
- **Draft validator, option A** (2026-10-21, split on 2026-10-24, #206): the server refuses
  credential-shaped values only, and the client keeps masking spoken passwords before anything leaves
  the machine. The instruction rule stays exactly production's. Both validators read one shared rule set
  with the same text normalisation. A redesign of instruction detection is its own piece of work, later.
- **INV-4 and the hidden pill** (2026-10-25, delegated by the owner: "go with the most practical"):
  between sessions only, a technician who hides the pill is choosing tray-only, so the UI reports a
  zero-size indicator and the next start is accepted. Once a session runs the pill is forced back and
  must really be on a monitor, or reports stop and the service suppresses. Rejected: forcing the pill on
  before any start (slower, a new round trip, no gain), the earlier behaviour (one Hide refused every
  later start until the UI restarted), and removing Hide.
- **Six ADRs Accepted:** 0001 (.NET stack), 0002 (platform-neutral Core), 0003 (per-user process,
  three-check pipe handshake), 0004 (a frame nobody read is not stored), 0005 (Windows adapters shared by
  both processes), 0006 (an unredacted frame waits in memory). **Two Proposed**, waiting for the owner:
  0007 (the Windows installer) and 0008 (auto-update).
- **The repository is public on purpose**: hosted Windows minutes are free for public repositories.
  Three repository settings, not workflow files, are what keep a stranger's pull request off the laptop.

## 5. Open items for the owner

- [ ] **Run M1.** On the laptop: `scripts\windows\demo\setup.cmd` once, then `start.cmd`, one session,
      and the rehearsal checklist at the end of `docs/dev/demo.md`. Write down what the first note got
      right and wrong. Drafting with Claude needs credits on the Anthropic account; with Gemini the free
      tier is enough. If anything goes wrong, run `report.cmd` before `stop.cmd` and keep the file.
- [ ] **Choose cloud hosting** (ST-007). Nothing is deployed until then; PostgreSQL on a laptop is fine
      for M1 and the demo.
- [ ] **Get code signing** for the installer (ST-112): Azure Artifact Signing is recommended, and
      `docs/dev/installer.md` says how to switch it on. Read ADR-0007 (Proposed) first; it lists the
      decisions it needs.
- [ ] **Set up auto-update** (ST-113), once signing and hosting exist: the update signing key in Azure
      Key Vault, its public key and the feed and package hosts in `shared/release/update-trust.json` by
      pull request, the `signing` environment's update settings, and the `release-stable` environment
      with you as its required reviewer. ADR-0008's "Owner steps" and `docs/dev/installer.md`, "Updates",
      list each one. Then check Windows' install prompt on the laptop with the first signed build.
- [ ] **A ConnectWise sandbox or API member** for the real check of ST-091 to ST-094
      (`docs/integrations/connectwise.md`). The demo PSA does not replace it.
- [ ] **A Hudu API key** for the real check of ST-095 to ST-097.
- [ ] **Enable billing on the Gemini key.** The free tier (20 requests a day, 5 a minute) is enough for
      M1 and the demo, not for the two measurements ST-063 still owes: the thinking-token budget and the
      repair rate.
- [ ] **Measure Claude once** with its key: `SCREENTAIL_LIVE_LLM=1` runs the live drafting test, for the
      time and cost of a heavy session (ST-063).
- [ ] **A lawyer's read** of `docs/legal/consent-guidance.md`, the pilot agreement and the privacy
      policy (ST-111).
- [ ] **A pilot MSP and technicians:** the baseline interviews (ST-110, with the market scan's §6
      question in the same conversations), five technicians for the usability round (ST-017), two
      sessions on the Review wireframe (ST-014), and the pilot itself (ST-116).
- [ ] **Have an MSP security lead read the privacy pack** (ST-114 AC1). It is unreviewed and says so.
- [ ] **Record the ten-minute narration** for ST-027's WER number (`research/fixtures/audio/README.md`).
- [ ] **ScreenConnect trial and a second Windows machine** for ST-030's golden sessions and ST-001 AC2.
- [ ] The Windows half of the accessibility audit with Accessibility Insights (ST-084).
- [ ] **Laptop checks from the 2026-10-29 merges:** a narrated session with the service log in view, to
      see whether the processor limit trips (`docs/dev/performance.md`, "Owner step on the laptop"); the
      loop harness with real clicks (`docs/dev/e2e.md`); and a look at the new UI where CI does not render
      it (dropdowns, tool tips, hover and pressed states, the typed-confirmation dialogs, the enlarged
      frame, Narrator).

### Decisions waiting for the owner

Each has a default in place, fail-closed where it matters, and the pull request or ADR that chose it
says so.

- **Pause wording** (#211): the pill and tray read "Paused: ..." with a colon, where the spec has a dash.
  Confirm, or give the wording.
- **Idle sessions in a remote tool** (#216): a session runs for as long as its remote tool stays in
  front; before #216 it was stopped after the grace. Should there be an input-idle limit?
- **How elevation is judged** (#216): a window is elevated when its integrity level is above the
  service's own. Should the service also check its own token's elevation type, so the answer is the same
  however it was started?
- **Waiting for the first policy** (#213): drafts, publishes and metrics wait until the first policy
  fetch with a device token has landed. Confirm.
- **An outbox with no backend** (#215): with no backend configured, a queued item is failed after a day
  and can be retried by hand, rather than waiting quietly. Confirm, or choose waiting.
- **A publish that may have landed** (ST-090): whether Retry on an `unknown` row is hidden, relabelled or
  confirmed (today it is offered under a message that says to check first), and a time budget for the
  whole publish.
- **The local text model** (ST-065): llama.cpp and a model download on the technician's machine.
- **The installer and auto-update** (ADR-0007, ADR-0008, both Proposed): how an uninstall treats
  technicians who are not signed in (their store is left unreadable rather than deleted); the update
  apply model and its defaults (stable channel, a check every 24 hours, no checks under local-only unless
  the admin allows it); and **no time limit on the install's hold**: while Windows Installer runs no
  session starts, however long the prompt is left open. ADR-0008's "Decisions for the owner" lists the
  rest.
- **The processor limit** (#232, `docs/dev/performance.md`): how the budget reads on a machine without
  four cores (15% of a smaller machine, 60% of one core on a bigger one); that the deepest level stops
  scene sampling rather than pausing OCR; and the step-back numbers (30 seconds under 80% of the budget).
- **A UI that misses a state change** (#231, INV-4): it keeps reporting the pill as put away during a
  session, and capture is now suppressed until a real pill report arrives. That fails closed, but the
  technician may see capture stop with no obvious reason. Confirm.
- **Settings and unsaved edits** (#211, #234): leaving Settings with unsaved edits and coming back reads
  every panel again, which discards them. Keeping them would be a new ticket.
- **Diagnostics counts that can say "not known"** (#229): they would reverse #213's "zeros are honest"
  choice and need an IPC version bump, so they wait for a decision.
- **The web admin's defaults** (ST-099, ADR-0009 Proposed). Sign-in is Google, as you chose. Built with
  fail-closed defaults for you to confirm: signup only for addresses on an operator-set list (empty:
  nobody); a new tenant starts with 5 seats; one owner per tenant and one tenant per Google account;
  invite codes shown once to the owner to hand on (no mail yet); 8-hour sessions with no refresh, and
  sign-out ending every session of that owner; integration status without even a credential's last
  four; no sign-in while Google's keys cannot be fetched. Since 2026-11-01 also: ten anonymous sign-in
  and activation calls a minute per address; no proxy trusted until listed; a 10-minute sign-in nonce;
  inline styles allowed in the page's CSP because Google's button needs them. For invite codes: 20
  characters (about 99 bits), a warning at 50 refused codes in 10 minutes, and the old ten-character codes
  accepted until they expire. ADR-0009 lists them all.
- **Mail** (ST-007, ST-010): which email service sends invites and the offboarding receipt.
- **The brand colour and logo** (#228): the Fluent blue and the text wordmark are placeholders. Also,
  accept spec amendment v0.5.0 in `Build Plan/01-UX-Design-Spec.md`, or say what to change.

## 6. Known gaps, deliberately left

- **ST-042 has no corpus recall gate**: it needs ST-030's staged captures.
- **OCR is a best-effort input to redaction, not a guarantee.** It will not read a long run of one
  ambiguous glyph. INV-1 cannot rest on the engine reading everything (ADR-0001 finding 9).
- **A 4K frame exceeds ST-025's budget and nothing fails**: about 309 ms against 120 ms, measured on the
  laptop. Windows.Graphics.Capture is the fix when it matters.
- **Each frame is decoded twice**, once to read it and once to paint on it. Passing the bitmap between
  them would hold about 33 MB a frame instead of 1.5 MB; a memory trade that was kept out of ADR-0006 on
  purpose and is its own decision when somebody wants it.
- **The full-disk case under-reports `frames_purged_unredacted`**: when the store refuses both the write
  and the record of the loss, the frame is let go rather than remembered, and the draft does not know to
  hedge. Named in the test; accepted as the price of not holding a frame for ever on a disk that will
  not empty.
- **Not done from the 2026-09-20 review, with reasons:** the hardware workflow as a reusable workflow
  (a laptop that is off would hang the run for a day instead of failing in 35 minutes; revisit if the
  repository goes private); a model hash cache (weakens the one check between a corrupt download and
  native code); the SQLite `cache_size` pragma (needs a laptop measurement, not reasoning);
  `run-local.ps1` deleting its output directory without asking (the directory is documented as
  disposable).
- **ST-006's frames are drawn, not captured.** Fine for UI and pattern work, useless for judging OCR.
- **ST-016's focus rings** were confirmed by code and CI render, not by tabbing through the app.
- **A throttled session can go without scene frames for long stretches** (#232): speech is never
  throttled, so steady narration can hold the service over budget. Clicks and marked moments are still
  photographed, and ST-026's "frame within 2 s" is suspended while throttled (`docs/dev/performance.md`).
- **A session waiting for its draft is not picked up again after a service restart** (#230), so it
  cannot be discarded from the tray then. Retention still ends it. A follow-up.

## 7. Next steps

| When | What | Who |
|---|---|---|
| **Now** | **M1**: one real session, one real note, on the laptop, with the demo scripts. | Owner |
| Right after | Write down what the first note got right and wrong, with the rehearsal checklist; that is the first row of the eval corpus, before ST-030's ten sessions exist | Owner, ten minutes |
| Then | Fix what M1 finds. Until then, small work that needs no owner: making the loop harness a required check once it is steady, the restarted draft-pending session (#230's follow-up), and ST-013's golden replay once ST-030 exists | Agent |
| Phase C proper | The real ConnectWise and Hudu checks (ST-091 to ST-097) | Agent, once there is a ConnectWise sandbox or API member and a Hudu key |
| In parallel, when the inputs exist | ST-027's WER number (the recording), ST-030 and ST-062 (ScreenConnect and a second machine), ST-063's two Gemini measurements (billing) and Claude's (its key) | Owner supplies; agent runs |
| Phase D | Hosting (ST-007), signing the installer (ST-112) and switching on auto-update (ST-113), both built; the web admin (ST-099), the security-lead review, the legal read, the usability round, the pilot | Both |

**Why this order.** The product has never drafted a note from a real session. M1 is the one thing that
tells us whether the next month goes into the pilot or into fixing what the first note got wrong, and
the demo scripts make it a short sitting at the laptop. Everything after it either needs the owner's
accounts and people or should wait for what M1 shows.

### Needs a decision or an account

ST-007 needs a cloud account and a hosting choice. ST-112 needs a code-signing certificate. ST-113 needs
the update signing key, the hosting and the `release-stable` environment. ST-091 to ST-094 need a
ConnectWise sandbox or API member, and ST-095 to ST-097 a Hudu key. ST-063's measurements need Gemini
billing and the Claude key. ST-111 needs a lawyer. ST-017, ST-110, ST-114's review and ST-116 need
technicians, a pilot MSP and a security lead.

## 8. Handy commands

| Want to... | Do |
|---|---|
| Run client tests | `dotnet test client/ScreenTail.sln` |
| Run backend tests | `dotnet test backend/ScreenTail.Backend.sln` (SQLite; never calls a model) |
| Run research tests | `cd research && pytest` |
| Set up the demo on the laptop (once) | `scripts\windows\demo\setup.cmd` |
| Start and stop the demo | `scripts\windows\demo\start.cmd`, then `scripts\windows\demo\stop.cmd` |
| Make one file to send when the demo is not working | `scripts\windows\demo\report.cmd`, before `stop.cmd` |
| Run one session through the whole loop, unattended | `scripts\windows\e2e\run-e2e.cmd` (`docs/dev/e2e.md`) |
| Get the first real note by hand | `docs/dev/first-draft-end-to-end.md` |
| Run the product on the laptop without the demo scripts | `powershell -ExecutionPolicy Bypass -File .\scripts\windows\run-local.ps1` |
| See which secrets the backend has, without retyping them | `dotnet user-secrets list --project backend/src/ScreenTail.Api` (prints values: mind the shoulder) |
| Render the component gallery | on Windows: `dotnet run --project client/ScreenTail.UI -- --gallery` |
| Render the note pane or the HUD | on Windows: `dotnet run --project client/ScreenTail.UI -- --note --screenshot <dir>` or `-- --hud --screenshot <dir>` |
| Read the closed weakness review | `docs/review/weaknesses.md`; the status block at the top says what is left |
| See the market and the idea list | `docs/product/2026-09-15-market-scan-and-ideas.md`: §2.4 for the wedge, §4 for ideas |
| Look at a fixture session | `research/fixtures/handcrafted/<name>/session.json` and its `frames/` |
| Regenerate schema or token outputs | `npm run codegen` in `shared/schema` or `shared/design` |
| Open the wireframes | `open docs/ux/wireframes/index.html` |
| Trigger a laptop spike run by hand | `gh workflow run spike-windows.yml --ref main` |
| See what the laptop measured | `gh run download <run-id>` then read the text files; images are never uploaded |
| Check the audit log verifies | `store.VerifyAuditAsync()`; the export says so too |
| Get the laptop ready for a CI run | `docs/dev/windows-test-loop.md`, "Before a laptop run" |
