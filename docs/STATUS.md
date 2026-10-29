# ScreenTail: project standing

**Snapshot taken:** 2026-10-27, after the client P2 run (#207 to #216) and its follow-ups (#220)
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

**The loop has run end to end on a Windows VM, unattended (2026-10-28, ST-013).** `scripts/windows/e2e/`
runs the backend, a development device, the capture service, the UI and one session through to the note
on the demo PSA's ticket, with a scripted drafting provider in place of the model, and says PASS or FAIL
for each step (`docs/dev/e2e.md`). Its first runs found the likeliest reason the demo "was not working properly": every
session with a backend ended on "draft failed: no summarization provider is configured" and never
reached "Draft ready", although its draft arrived. That is fixed, with two smaller finds. It does not
replace M1: the model's note and a person reading it are still owed.

**M1 is two commands now.** `scripts/windows/demo/` (#219) sets up, starts and stops the whole loop on
the laptop: the development certificate, the Gemini key, the signing key, PostgreSQL, the backend, a
development device token, the capture service and the UI, each checked and each failure said in plain
words. `docs/dev/demo.md` is the guide, with a demo story and a rehearsal checklist of what has never
been seen on a real machine. The manual step list, `docs/dev/first-draft-end-to-end.md`, was corrected in
the same PR and does the same thing by hand. The scripts themselves have not yet been run end to end by a
person.

**A demo PSA stands in for ConnectWise in Development** (#218, `docs/dev/demo-psa.md`), so a
demonstration can end with the note on a ticket while there is no ConnectWise sandbox. It is named
"Demo PSA (not ConnectWise)" wherever it shows, the backend refuses to start with it outside
Development, and it proves the publish path, not ConnectWise.

**The 2026-10-12 review (efficiency, security and regression tests) is worked through.** Its 42
pre-pilot findings landed as #173 to #195 by 2026-10-23. The backend P2 findings landed as #199 to #202
and #206. The client P2 findings landed in nine groups between 2026-10-25 and 2026-10-27: #207 (pipe),
#208 (sessions, input, CI), #210 (review and publish), #211 (shell and HUD), #212 (speech), #213 (store
and host), #214 (redaction), #215 (settings and network) and #216 (capture and detection). Each was
written test first with a sabotage check and read by an adversarial reviewer before it got a pull
request, and eight of the nine waited for the laptop's hardware jobs. The fixes that crossed groups
landed together as #220. The findings themselves stay in an untracked local file. What the run left
for the owner to decide is in §5.

**Of 85 tickets, 66 are Done or built, 7 are Partial and 12 are Open** (counted from the Status lines on
2026-10-28).

- **45 are done outright.**
- **21 are built with a named part still owed:** ST-091, ST-092, ST-095 and ST-096 are done against
  recorded shapes and owe the real ConnectWise and Hudu checks; ST-093, ST-094 and ST-097 are done on the
  server side; ST-073, ST-076, ST-077, ST-078 and ST-085 are done on the Mac side; ST-080, ST-081, ST-082
  and ST-083 are built and not yet seen on a real screen; ST-010 (the device side), ST-062 (the harness;
  the corpus is owed), ST-065 (the on-device draft; the local model is owed), ST-084 (the static half)
  and ST-115 (round one) each owe their second half.
- **7 are Partial,** and each says what it waits for: ST-013 (the loop harness runs in CI; the golden
  replay, resource figures and the nightly schedule are owed), ST-014 (two technician sessions), ST-027 (the WER
  number needs a human recording), ST-031 (measured and enforced on the laptop; the golden replay, the
  throttle and the nightly harness are owed), ST-042 (the recall gate needs ST-030's labelled frames),
  ST-112 (the installer is built and smoke-tested unsigned in CI; signing needs the certificate), ST-114
  (the security-lead review).
- **12 are Open:** ST-007 (hosting), ST-017 (the usability round),
  ST-030 (golden sessions), ST-099 (the web admin), ST-110, ST-111, ST-113 (auto-update) and ST-116 (the pilot), and the four v1.1 and v1.2 tickets
  ST-120 to ST-123.

Phases A and B of the ordered plan (backlog Part C) are complete apart from what needs the owner: ST-030
(golden sessions; a ScreenConnect trial and a second machine) and ST-062's corpus, which is ST-030's.

## 2. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (**public**) | https://github.com/hiratinspace/ScreenTail | `main` green at #220, no open pull requests |
| Build plan | `Build Plan/` | Spec at **v0.4.3**. Backlog Part C is the ordered plan, Part D the amendments |
| Client | `client/` | `.Shared` (schema and IPC), `.Core` (platform-neutral logic), `.Platform` (Windows adapters, ADR-0005), `.Service`, `.UI`. **2,273 tests** in the suite that runs anywhere and **246** in the Windows-only suite (CI on #220, ten of them skipped on the hosted runner by design) |
| Backend | `backend/` | ASP.NET Core API: tenants, devices, device tokens, invites, policy, the summarize and publish endpoints, the Gemini provider, the cost ledger, the credential vault, ConnectWise and Hudu clients, and the demo PSA. **962 tests** (two skipped). Runs locally on PostgreSQL; not deployed (ST-007) |
| Research | `research/` | Fixtures, note prompt v1 with its hardening cases, the shared validator rules, the WER script and the draft-quality harness. **255 tests** |
| Shared contracts | `shared/` | The session schema, the design tokens, and the wire contracts (summarize, publish, session metric, tenant policy) both sides test against |
| Demo setup | `scripts/windows/demo/`, `docs/dev/demo.md` | Setup, start and stop for the whole loop on the laptop (#219). Parse-checked in CI; not yet run end to end by a person |
| Windows test loop | Hosted Windows VM and the spare laptop `SCREENTRAIL` | Both run on every PR that touches capture. The laptop's jobs were green on #214, #218, #219 and #220 on 2026-10-27 |
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
  waits for a current indicator report and takes the same session policy (#220).
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
- **The drafting path is wired end to end.** Bundle, outbox, sender, `POST v1/sessions/summarize` with a
  device token, Gemini Flash, the validator (invented frames, fake quotations, credentials,
  instruction-shaped text; the backend's and the research copy share one rule set since #206), and the
  note in the store. One model call per session; the daily cap is checked before the call. Nothing is
  sent before the first policy fetch has landed (#213). A retryable outbox item is failed only after a
  day and eight attempts (#215). A local-only session is drafted on the device instead (#168).
- **The publish path is built end to end**: note, time entry and KB article from the Review pane, with
  the company mapping prompt (ST-097), the ticket pre-selected from the window (ST-077) and recent tickets
  on focus. None of it has met a real PSA; in Development the demo PSA can stand in (#218).
- Every HTTP call the service makes goes through the egress allowlist at the composition root. Anything
  that is not HTTPS, or not the model host or the configured backend host, is refused (INV-8).
- The audit log is hash-chained with a head anchor, so truncation from either end is detected. A draft,
  a publish and a metric each write a `bundle_sent` row with the host and the size, never the body (#220).
- Every process logs through one scrubbing sink (ST-011): content-carrying values are `[redacted]`,
  paths `[path]`; the backend's scrubber knows every name the client's does (#220). A crash leaves a
  stack-only report, and only when opted in.
- Design tokens generate WPF dictionaries and web CSS from one source; the gallery renders in CI in
  dark, light and high contrast.

**Not built:** the microphone picker and level meter in Settings, Capture (ST-080's AC3); the local text
model (ST-065); the installer's signature (ST-112 is built and smoke-tested unsigned, ADR-0007) and
auto-update (ST-113); hosting (ST-007); the web admin
(ST-099); mail for invites (ST-010 prints the code for the operator to hand on).

### Seen on real hardware, and not

**Seen:** the Windows-only suite and the input-injection job run on the laptop for every pull request
that touches capture, so hooks, foreground detection, screen capture, OCR, the pipe's handshake and the
latency and performance budgets are checked on a real desktop, not only on a cloud VM. ST-001's spike was
measured there. The drafting call was measured against the real Gemini model on 2026-09-19 (ST-063), on
the heaviest bundle a client may send, not on a recorded session.

**Not seen yet:**

- The full loop: a real session drafted by the real model and read in Review. That is M1.
- The demo scripts run end to end by a person.
- The tray and pill following a real session with the service up, and screenshots over a remote window
  (ST-085's own walk-through).
- Review, History, Settings and the onboarding wizard on a real screen. CI renders them over fixtures in
  three themes.
- A publish to ConnectWise or Hudu. Both clients are tested against recorded shapes only.
- The speech model's word error rate on a real narration (ST-027), and redaction recall on real captures
  (ST-042).
- RDP opacity from a second machine (ST-001 AC2), the Narrator and keyboard pass (ST-084), the toasts on
  Windows (ST-073), and an installed, signed build.

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
  2026-09-19. Anthropic and OpenAI stay swappable behind the same interface.
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
- **Six ADRs, all Accepted:** 0001 (.NET stack), 0002 (platform-neutral Core), 0003 (per-user process,
  three-check pipe handshake), 0004 (a frame nobody read is not stored), 0005 (Windows adapters shared by
  both processes), 0006 (an unredacted frame waits in memory).
- **The repository is public on purpose**: hosted Windows minutes are free for public repositories.
  Three repository settings, not workflow files, are what keep a stranger's pull request off the laptop.

## 5. Open items for the owner

- [ ] **Run M1.** On the laptop: `scripts\windows\demo\setup.cmd` once, then `start.cmd`, one session,
      and the rehearsal checklist at the end of `docs/dev/demo.md`. Write down what the first note got
      right and wrong. The free Gemini tier is enough for this.
- [ ] **Choose cloud hosting** (ST-007). Nothing is deployed until then; PostgreSQL on a laptop is fine
      for M1 and the demo.
- [ ] **Get code signing** for the installer (ST-112): Azure Artifact Signing is recommended, and
      `docs/dev/installer.md` says how to switch it on. Read ADR-0007 (Proposed) first; it lists the
      decisions it needs.
- [ ] **A ConnectWise sandbox or API member** for the real check of ST-091 to ST-094
      (`docs/integrations/connectwise.md`). The demo PSA does not replace it.
- [ ] **A Hudu API key** for the real check of ST-095 to ST-097.
- [ ] **Enable billing on the Gemini key.** The free tier (20 requests a day, 5 a minute) is enough for
      M1 and the demo, not for the two measurements ST-063 still owes: the thinking-token budget and the
      repair rate.
- [ ] **A lawyer's read** of `docs/legal/consent-guidance.md`, the pilot agreement and the privacy
      policy (ST-111).
- [ ] **A pilot MSP and technicians:** the baseline interviews (ST-110, with the market scan's §6
      question in the same conversations), five technicians for the usability round (ST-017), two
      sessions on the Review wireframe (ST-014), and the pilot itself (ST-116).
- [ ] **Have an MSP security lead read the privacy pack** (ST-114 AC1). It is unreviewed and says so.
- [ ] **Record the ten-minute narration** for ST-027's WER number (`research/fixtures/audio/README.md`).
- [ ] **ScreenConnect trial and a second Windows machine** for ST-030's golden sessions and ST-001 AC2.
- [ ] The Windows half of the accessibility audit with Accessibility Insights (ST-084).

### Decisions waiting for the owner

Each has a fail-closed default in place and the pull request that chose it says so.

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

## 7. Next steps

| When | What | Who |
|---|---|---|
| **Now** | **M1**: one real session, one real note, on the laptop, with the demo scripts. | Owner |
| Right after | Write down what the first note got right and wrong, with the rehearsal checklist; that is the first row of the eval corpus, before ST-030's ten sessions exist | Owner, ten minutes |
| Then | Fix what M1 finds. Until then, small work that needs no owner: diagnostics counts that can say "not known" (an IPC version bump and the UI side), ST-013's golden replay once ST-030 exists, and ST-031's throttle (AC2) | Agent |
| Phase C proper | The real ConnectWise and Hudu checks (ST-091 to ST-097) | Agent, once there is a ConnectWise sandbox or API member and a Hudu key |
| In parallel, when the inputs exist | ST-027's WER number (the recording), ST-030 and ST-062 (ScreenConnect and a second machine), ST-063's two measurements (billing) | Owner supplies; agent runs |
| Phase D | Hosting (ST-007), the signed installer (ST-112) and auto-update (ST-113), the web admin (ST-099), the security-lead review, the legal read, the usability round, the pilot | Both |

**Why this order.** The product has never drafted a note from a real session. M1 is the one thing that
tells us whether the next month goes into the pilot or into fixing what the first note got wrong, and
the demo scripts make it a short sitting at the laptop. Everything after it either needs the owner's
accounts and people or should wait for what M1 shows.

### Needs a decision or an account

ST-007 needs a cloud account and a hosting choice. ST-112 needs a code-signing certificate. ST-091 to
ST-094 need a ConnectWise sandbox or API member, and ST-095 to ST-097 a Hudu key. ST-063's measurements
need Gemini billing. ST-111 needs a lawyer. ST-017, ST-110, ST-114's review and ST-116 need technicians,
a pilot MSP and a security lead.

## 8. Handy commands

| Want to... | Do |
|---|---|
| Run client tests | `dotnet test client/ScreenTail.sln` |
| Run backend tests | `dotnet test backend/ScreenTail.Backend.sln` (SQLite; never calls a model) |
| Run research tests | `cd research && pytest` |
| Set up the demo on the laptop (once) | `scripts\windows\demo\setup.cmd` |
| Start and stop the demo | `scripts\windows\demo\start.cmd`, then `scripts\windows\demo\stop.cmd` |
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
