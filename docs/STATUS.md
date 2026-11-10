# ScreenTail: project standing

**Snapshot taken:** 2026-11-10, of `main` at `31afeac` (#268). It covers every merge since the
2026-11-04 snapshot (#255 to #266 and #268, the last of them on 2026-11-09): the fixes that came out of
the owner's first runs on the laptop (the recording clock, the elevated window), a publish that may have
landed, a real leak in on-screen redaction, keyboard blur, the web admin's browser tests, the stop time
measured automatically, and three smaller ones.
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next.
**Source of truth:** the **Status** line on each ticket in `Build Plan/02-Backlog-v0.4.md`. This page summarises; the backlog decides. `docs/README.md` says which document is which.

---

## 1. Where things stand

**Everything between a real session and a published note is built, merged and green. It is not
established that any of it has run end to end with the real model and a person reading the note.**
Capture, redaction, speech, the bundle, the outbox, the sender, the backend endpoint, the model call,
Review and the publish path each have tests, and the capture-path tests run on the spare laptop on every
pull request that touches them. Built is not proven. A run with a real session, a real backend, a real
key and a person reading the note, all at the same time, is **M1**, and it is still owed.

**The owner has now run the demo scripts on the laptop by hand:** setup, start and a recorded session.
Those runs are where two of this stretch's fixes came from: the recording clock that never moved (#262,
reported 2026-11-05) and `start.cmd` in an administrator window, where the capture service refused to
run elevated and the UI never opened (#264). Nothing in the repository records a run that used the real
model with a person reading the note, so the rehearsal checklist in `docs/dev/demo.md` and the note
about what the first draft got right and wrong are still to do.

**Pull `main` before the next run on the laptop.** The pipe contract is version 9 since #265, and the
service and the UI must match it exactly; ten-character invite codes are refused since #260. `start.cmd`
builds from the clone, so a clone at `main` gives one build of both. A clone from before #264 will still
start in an administrator window and fail late.

**What landed since 2026-11-04:**

- **The recording clock counts** (#262, ST-072). The owner's report from a real run: the pill said
  recording and its time never changed. The service sends a snapshot only when its state changes, and
  nothing in the UI ticked. The pill, the tray and the shell's status line now share one clock that
  counts locally once a second while the service says recording, and stops when it says anything else or
  stops answering. The redaction count is sent when it moves. Past the hour the tray and status line read
  `h:mm:ss`. The message shapes did not change.
- **A stop never waits for a transcription in flight** (#262, #263; ST-112, ST-027; the owner's decision
  of 2026-11-05). With sound at the laptop's microphone a sign-out had run past the four-second budget
  twice, the speech model's disposal waiting for a native call. The call is now told to abort, the
  unfinished narration segment is dropped (never half-kept) and counted in the service log, and disposal
  waits at most 500 ms. The laptop holds it with ten minutes of audio in flight: on #263's final head
  (run 42526106737) disposal returned in 505 ms against a limit of 650, and a process left with a call
  still running exited in 140 ms.
- **A UI that missed a state change is reconnected** (#263). A `state_changed` the service cannot write
  to a client within five seconds now closes that client's connection; the UI shows the state as unknown
  and reconnects, and the reconnect carries the current state. Before, it kept saying Recording.
- **`start.cmd` refuses an administrator window before building anything** (#264), with the fix in plain
  words. `setup.cmd` only warns, and `report.cmd` says which kind of window it ran in. Parse-checked and
  tested in CI; the four steps to try by hand are in #264 and not yet reported back.
- **A publish that may have landed is held until the ticket is checked** (#265, ST-093, contract v9).
  Before, a connection to the service that dropped after the publish went out read as failed, and
  Publish would post the note a second time. Now a lost answer, and the backend's `unknown` and
  `timed_out` rows, are "not confirmed" for each destination, and Publish, Ctrl+Enter and Retry wait for
  an "I checked ticket #..." checkbox, spent on one send. The service keeps what the last publish left
  for each destination (migration 0013), across a restart, and refuses a plain resend of one it holds as
  landed or unknown. A reopened Review offers each destination as the last publish left it. Spec
  amendment v0.5.2.
- **On-screen redaction masks every piece of a token a match touches** (#268, ST-042). This was a real
  leak, found by the loop harness: Windows OCR sometimes reads one on-screen password as two words, the
  password rule ended at the first plain word, and the second piece stayed in the stored text and
  unpainted in the stored frame. The hosted loop caught it in 4 of 72 runs between 2026-11-04 and
  2026-11-08. A match now takes every word on its row that is closer than a space, and a match with a
  password cue masks every row it touches, the cue included, keeping only the words wholly to the left
  of the cue on the cue's own row (the owner's decision of 2026-11-09). The Windows probe on `main`'s head: 1,000 renderings at each of four sizes, 9 to 130 of
  them split by the engine, none left showing in the text or the frame. It costs readability: the rule
  also takes 16 to 69 percent of real spaces beside a match, most at 9pt. What it does not cover is in §6.
- **A region can be blurred from the keyboard** (#257, ST-084, spec amendment v0.5.1). On the enlarged
  screenshot B brings out a box, the arrows move it, Shift with them resizes it, Enter blurs through the
  mouse drag's own path and Esc puts it away, with its position announced in percent. Publish is held
  while a blur is still being applied, and a blur whose answer was lost says so and never "nothing was
  blurred".
- **The web admin is driven in a real browser in CI** (#255, #259, ST-099): Chromium against the real
  backend on PostgreSQL, from sign-in through signup, the invite shown once, a device activating, revoke,
  the policy editor and sign-out, with the security headers, zero CSP violations and axe-core on every
  page. Google is a stand-in signed with a key made for the run, which the backend trusts only in
  Development. It runs when `web/` or the backend's code changes. A sign-in with a real Google account
  is still owed.
- **The service's stop time in the middle of a recording is measured on every Windows run** (#258,
  ST-112), with a frame still waiting for redaction at the ask: 63 to 100 ms on the hosted runner in
  `main`'s latest run, 168 ms for the sign-out case on the laptop (run 42526106737), against 4 s.
- **Smaller merges:** the old ten-character invite codes are refused like any malformed code (#260,
  ADR-0009 decision 23); the title hook's refusal count is shown in diagnostics as "Hook fallbacks"
  (#261, contract v8); the deploy smoke test reads port bindings from `docker inspect`, after Docker
  Compose 5 on the new hosted image made the old check wrong (#256); and the backend tests' SQLite
  driver is pinned past a pool race that had failed one run on `main` (#266; test-only, production runs
  on PostgreSQL).

**In progress, not on `main`.** One draft pull request is open: #269, two follow-up commits to #268 (a
row of zero-height boxes, and a very tall box beside a row). Its one laptop run so far (42721033883)
ended in the Windows tests step without a result, and it waits for another. Two more pieces are being
worked on and not yet opened: a "Not confirmed" label with a "Mark as published" action for a publish
nobody could confirm (ST-093), and OCR hardening for display scaling, a scaled lossy remote viewer and
secrets that wrap across lines (ST-042).

**The laptop runner is online only when the owner turns it on.** It was offline when this snapshot was
taken. A pull request that touches capture queues its laptop jobs until then, and its `ci-ok` stays red
meanwhile.

**The loop runs end to end on a Windows VM, unattended** (#230, ST-013). `scripts/windows/e2e/` runs the
backend, a development device, the capture service, the UI and one session through to the note on the
demo PSA's ticket, with a scripted drafting provider in place of the model, and says PASS or FAIL for
each step (`docs/dev/e2e.md`). On `main` it failed once since the last snapshot, at `20d2471` (run
42524521759), on the leak #268 then fixed, and it has passed on the four runs since (20 of 20 steps in
the latest). Three of those four ran before the fix, so they show the leak was intermittent, not that
it was gone; the probe above is the evidence for that. The job is still report-only, and making it a
required check is owed. The laptop's run with real hotkeys and clicks has not been seen to pass. It does
not replace M1: the model's note and a person reading it are still owed.

**M1 is two commands.** `scripts/windows/demo/` (#219) sets up, starts and stops the whole loop on the
laptop, each step checked and each failure said in plain words; `docs/dev/demo.md` is the guide, with a
demo story and a rehearsal checklist. `report.cmd` (#227) writes one plain-text file to send when
something goes wrong, with secrets, email addresses and user paths removed. The manual step list is
`docs/dev/first-draft-end-to-end.md`.

**Still true from the last snapshot.** The web admin is built, hardened, and off until a Google OAuth
client id is set (#245, #246, ADR-0009 Proposed). Invite codes are twenty characters, about 99 bits
(#248). A deployment exists for any Linux host with Docker, and no host is chosen (#249,
`docs/dev/deploy.md`). The installer waits for other programs' open files, and the service closes when
Windows asks and refuses to start elevated (#247, #250, #251); signing still waits for the certificate.
The microphone is chosen in Settings with a live level (#244), not yet tried with a real headset. The
idle service's hooks are narrowed (#252). Accessibility is checked by CI on a real Windows desktop
(#253); the Narrator listening pass still needs a person. Claude can draft instead of Gemini (#225,
ST-063), not yet measured against the real model. A demo PSA stands in for ConnectWise in Development
(#218, `docs/dev/demo-psa.md`), and the backend tests drive the real ConnectWise and Hudu providers over
HTTPS against stand-in servers (#226); neither proves the vendors accept it. The client has a modern
Windows 11 look (#228, tokens v0.5.0), with a placeholder accent and wordmark. Auto-update is built and
switched off (#233, ST-113, ADR-0008 Proposed). The processor limit is built and proved on a fake clock
(#232, ST-031 AC2). The 2026-10-12 review is worked through: its 42 pre-pilot findings landed as #173 to
#195, the backend P2 findings as #199 to #202 and #206, and the client P2 findings in nine groups, #207
to #216, with the cross-group fixes in #220.

**Of 85 tickets, 67 are Done or built, 9 are Partial and 9 are Open** (recounted from the Status lines
on 2026-11-10). The numbers are the same as on 2026-11-04, and no ticket changed group: this stretch
fixed and hardened tickets that were already built. ST-072 stays Done with its clock fixed; ST-084,
ST-093 and ST-099 stay built with a named part owed; ST-013, ST-027, ST-031, ST-042 and ST-112 stay
Partial.

- **45 are done outright.**
- **22 are built with a named part still owed:** ST-099 (the web admin) owes a sign-in with a real
  Google account and the owner's OAuth client id, and a host for `deploy/`; ST-091, ST-092, ST-095 and
  ST-096 are done against recorded shapes and owe the real ConnectWise and Hudu checks; ST-093, ST-094
  and ST-097 are done on the server side (ST-093's client now holds a publish that may have landed; the
  outbox-key idempotency in its description is still not built); ST-073, ST-076, ST-077, ST-078 and ST-085 are done on the Mac
  side; ST-080, ST-081, ST-082 and ST-083 are built and not yet seen on a real screen (ST-080's
  microphone not yet heard on a real headset); ST-010 (the mail provider and three ADR-0009 decisions),
  ST-062 (the harness; the corpus is owed), ST-065 (the on-device draft; the local model is owed), ST-084
  (everything automatable, keyboard blur included; the Narrator listening pass and a FastPass report are
  owed) and ST-115 (round one) each owe their second half.
- **9 are Partial,** and each says what it waits for: ST-007 (the deploy package is built and
  smoke-tested; the host, the name, the mail provider and the cloud half are owed), ST-013 (the loop
  harness runs in CI, report-only; a passing laptop run with real clicks, making it required, the golden
  replay, resource figures and the nightly schedule are owed), ST-014 (two technician sessions), ST-027
  (the WER number needs a human recording), ST-031 (budgets enforced on the laptop, the throttle built,
  the idle hooks narrowed and their fallback count shown; the golden replay, the throttle seen on the
  laptop, the nightly harness, memory with speech and the other live figures in "What's being captured"
  are owed), ST-042 (the split-token leak is fixed; the recall gate needs ST-030's labelled frames, and
  scaled or lossy captures and wrapped secrets are not yet covered), ST-112 (the installer is built and
  smoke-tested unsigned in CI, and the mid-recording stop is measured on every Windows run; signing needs
  the certificate, and an elevated installer closing the service is to be seen on the laptop), ST-113
  (auto-update is built and tested; switched off until the owner creates the key, the hosting and the
  release environment), ST-114 (the security-lead review).
- **9 are Open:** ST-017 (the usability round), ST-030 (golden sessions), ST-110, ST-111 and ST-116 (the
  pilot), and the four v1.1 and v1.2 tickets ST-120 to ST-123.

Phases A and B of the ordered plan (backlog Part C) are complete apart from what needs the owner: ST-030
(golden sessions; a ScreenConnect trial and a second machine) and ST-062's corpus, which is ST-030's.

## 2. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (**public**) | https://github.com/hiratinspace/ScreenTail | `main` green at #268 (`31afeac`, CI run 42717653493); one open pull request on 2026-11-10, the draft #269 |
| Build plan | `Build Plan/` | Spec at **v0.4.3** accepted; the amendments v0.5.0 (the modern look, #228), v0.5.1 (keyboard blur, #257) and v0.5.2 (a publish that is not confirmed, #265) wait for the owner. Backlog Part C is the ordered plan, Part D the amendments |
| Client | `client/` | `.Shared` (schema and IPC, contract version 9), `.Core` (platform-neutral logic), `.Platform` (Windows adapters, ADR-0005), `.Service`, `.UI`, and the accessibility scanner's own process `.Tests.AxeRunner`. **3,263 tests** in the suite that runs anywhere and **435** in the Windows-only suite (CI run 42717653493 on `31afeac`; ten of the 435 skipped on the hosted runner by design, which is the skip budget) |
| Backend | `backend/` | ASP.NET Core API: tenants, devices, device tokens, invites, policy, the summarize and publish endpoints, the Gemini and Claude providers, the cost ledger, the credential vault, ConnectWise and Hudu clients, the demo PSA, the update policy fields, owner sign-in with Google and the web admin's routes (ST-099) with their rate limit, sign-in nonce and security headers, and the invite-code guard and activation lock (#248). **1,419 tests** (two skipped; CI run 42524568773 on `3a3bb99`, #266, the last change to the backend). Runs locally on PostgreSQL; not deployed (ST-007) |
| Deployment | `deploy/`, `docs/dev/deploy.md` | Docker Compose for one Linux host: Caddy with automatic HTTPS, the API, PostgreSQL and the web admin from one origin (#249). Built and checked by `deploy/smoke.sh` in CI's Backend job; no host or name yet |
| Research | `research/` | Fixtures, note prompt v1 with its hardening cases, the shared validator rules, the WER script and the draft-quality harness. **255 tests** (CI run 42717653493) |
| Web admin | `web/`, `docs/dev/web-admin.md` | React and TypeScript on the shared tokens: Google sign-in, first-run tenant, Technicians and seats, Policy, Integrations, Download client (ST-099), with its security headers in `web/security-headers.ts`. **44 unit tests** and **5 browser tests** (Playwright in Chromium against the real backend, #255; both in CI run 42524568773). Lighthouse accessibility 100 as measured by hand for #245 and #246 (2026-10-31 and 2026-11-01); Lighthouse is not in CI and was not rerun |
| Shared contracts | `shared/` | The session schema, the design tokens, and the wire contracts (summarize, publish, session metric, tenant policy, invite code) both sides test against |
| Demo setup | `scripts/windows/demo/`, `docs/dev/demo.md` | Setup, start, stop and the diagnostic report for the whole loop on the laptop (#219, #227, #237, #241, #264). Parse-checked in CI. The owner has run setup, start and a recorded session by hand; the rehearsal checklist with the real model is still owed (§1) |
| Loop harness | `scripts/windows/e2e/`, `docs/dev/e2e.md` | One session through the whole loop with a scripted drafting provider (#230). On the hosted VM it passed in `main`'s last four runs (20 of 20 steps in run 42717653493) and failed once before them, in run 42524521759, on the leak #268 fixed; report-only in CI |
| Installer and updates | `installer/`, `docs/dev/installer.md` | The MSI and setup program, smoke-tested unsigned in CI (#224): install, upgrades with another program holding the service executable, Restart Manager's close and restart, uninstall; **186 of 186 checks** in CI run 42717653493. The signed update channel (#233). Signing and updates stay off until the owner sets them up |
| Accessibility suite | `client/ScreenTail.Tests.Windows/Accessibility`, `docs/ux/a11y-audit.md` | Every technician-facing screen, the blur box included (#253, #257), on the hosted runner and the laptop: 34 Accessibility Insights scans in CI run 42717653493. The Narrator pass is the part left for a person |
| Windows test loop | Hosted Windows VM and the spare laptop `SCREENTRAIL` | Both run on every PR that touches capture. The laptop is online only when the owner turns it on (offline on 2026-11-10), and its jobs queue until then. They were last green on #263's final head (42526106737) and #268's (42708659547). Since #253 its capabilities job brings test windows to the front for a few minutes, so leave the laptop alone while it runs (`docs/dev/windows-test-loop.md`) |
| Branch protection | Ruleset "main" | **On.** PR required, `ci-ok` must be green, no force-push, no deletion. The repository is public; three GitHub settings keep strangers' code off the laptop (`docs/dev/windows-test-loop.md`, Security) |

## 3. What the code does today

- The capture service starts per user, serves an authenticated named pipe (user ACL, verified client
  executable, per-run token in a file only that user can read), recovers what a crash left behind, and
  runs retention hourly with a vacuum only when a third of the file is free.
- **Both processes check themselves at every start** (#251): one that is elevated, running as a user
  other than the session's, or in session 0 writes a fixed sentence to the Application log and exits, so
  an elevated copy can never judge an administrator's windows as its own (INV-6).
- **The service closes when Windows asks** (#251): sign-out, shutdown, Restart Manager and `WM_CLOSE`
  reach a hidden window, the host stops the way every stop does, and the store is closed before Windows
  is told it may end the session (at most 4 s). A recording in progress is recovered as partial at the
  next start. Restart Manager may start the service again after another program's installer closed it;
  the restart watches and does not record.
- **The UI is connected** (ST-085). A tray icon and the recording pill show live state and say what is
  holding capture when it is paused (#211); the diagnostics window is filled by the service; discard and
  erase need a typed confirmation the service issued moments before. **Review, History, Settings and
  the onboarding wizard are real panes** fed over the pipe. Review has its Discard and History its bulk
  discard, by name, with a typed DISCARD or the count (#243). The UI tells the service every two seconds
  that the pill is on screen and where; the service treats a pill it has not heard from in six seconds
  as absent and suppresses capture until one is back (INV-4). Every start, automatic or over the pipe,
  waits for a current indicator report and takes the same session policy (#220). Once a session runs,
  only a report with the pill's real size counts (#231).
- **The UI is held to Spec §7 by tests** (#253): names, roles and label-in-name, 28 px targets (32 on
  the pill), Tab order without traps, a visible focus change in dark, light and high contrast, every
  documented shortcut (Ctrl+S, Alt+1/2/3, `?` for the shortcut sheet), Esc on every dialog, and live
  regions that are announced.
- The session state machine owns the lifecycle. Sources can only write while recording (INV-6). The
  input hooks are installed while a session exists and removed when it ends. Auto-start follows the
  remote tool in front, and the auto-stop grace runs from when the tool loses focus (#216). After a
  restart, the last session whose draft is still queued is adopted again, so it can still be discarded
  from the tray (#238).
- **At idle the service hears only what it needs** (#252): the window-rename hook follows the window in
  front (every thread while a classic console is in front), and the focus hook is in only while a session
  is under way. The foreground hook still hears every thread, so auto-start is unchanged.
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
- **Device activation** takes a twenty-character invite code only (the old ten-character codes are
  refused like any malformed code since 2026-11-04, decision 23), refuses a malformed code exactly as an
  unknown one, and claims the invite and the seat under a lock on the tenant's row (#248).
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

**Not built:** the local text model (ST-065); the cloud half of hosting (ST-007: Terraform, managed
PostgreSQL, a KMS, the release-feed bucket, a deploy with manual approval; `deploy/` already runs the
backend, database and web admin on one Linux host and waits for a host and a name); mail for invites (the
operator's CLI and the web admin show the code once, to hand on). **Built and switched off until the
owner sets them up:** the installer's signature (ST-112, ADR-0007), auto-update (ST-113, ADR-0008) and
owner sign-in to the web admin (ST-099, ADR-0009: off until a Google OAuth client id is set).

### Seen on real hardware, and not

**Seen:** the Windows-only suite and the input-injection job run on the laptop for every pull request
that touches capture, so hooks, foreground detection, screen capture, OCR, the pipe's handshake and the
latency and performance budgets are checked on a real desktop, not only on a cloud VM. The idle service's
wakeups with the narrowed hooks (#252) and the accessibility suite at the laptop's scaled display (#253)
were measured there. ST-001's spike was measured there. The drafting call was measured against the real
Gemini model on 2026-09-19 (ST-063), on the heaviest bundle a client may send, not on a recorded session.

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
- An elevated third-party installer closing the service on the laptop with UAC on, and the copy Restart
  Manager starts again then running at medium integrity or refused by the start guard (#251).
- Review, History, Settings and the onboarding wizard on a real screen, in the new look, read by
  Narrator (ST-084's listening pass). CI renders them over fixtures in three themes and checks them with
  the accessibility suite; dropdowns, tool tips, hover and pressed states are not rendered there.
- A sign-in to the web admin with a real Google account, and the deployment on a real host.
- A publish to ConnectWise or Hudu. Both clients are tested against recorded shapes and, since #226,
  stand-in servers only.
- The speech model's word error rate on a real narration (ST-027), and redaction recall on real captures
  (ST-042).
- RDP opacity from a second machine (ST-001 AC2), the toasts on Windows (ST-073), an installed, signed
  build, and an update installed through Windows' prompt.

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
- **Owners sign in to the web admin with Google** (the owner's choice, 2026-10-31; ADR-0009).
- **Six ADRs Accepted:** 0001 (.NET stack), 0002 (platform-neutral Core), 0003 (per-user process,
  three-check pipe handshake), 0004 (a frame nobody read is not stored), 0005 (Windows adapters shared by
  both processes), 0006 (an unredacted frame waits in memory). **Three Proposed**, waiting for the owner:
  0007 (the Windows installer), 0008 (auto-update) and 0009 (owner sign-in to the web admin).
- **The repository is public on purpose**: hosted Windows minutes are free for public repositories.
  Three repository settings, not workflow files, are what keep a stranger's pull request off the laptop.

## 5. Open items for the owner

- [ ] **Set the demo laptop up again from `main`** (§1): `scripts\windows\demo\setup.cmd`, then
      `start.cmd`. A client from before #248 refuses the new invite codes.
- [ ] **Run M1.** On the laptop: one session, and the rehearsal checklist at the end of
      `docs/dev/demo.md`. Write down what the first note got right and wrong. Drafting with Claude needs
      credits on the Anthropic account; with Gemini the free tier is enough. If anything goes wrong, run
      `report.cmd` before `stop.cmd` and keep the file.
- [ ] **Hand checks on the laptop:**
  - the microphone picker with a real headset, plugged and unplugged, and Win+L with the card open
    turning Windows' microphone light off (ST-080 AC3, #244);
  - no longer by hand: the service's stop time in the middle of a recording, with a frame still waiting
    for OCR and redaction, is measured automatically on every Windows run (`StopTimeTests`, ST-112;
    `docs/dev/installer.md`), 38 to 115 ms on the hosted runner against the 4 s budget. On the laptop it also
    requires the microphone and the speech model loaded (not transcribing: a quiet room gives Whisper
    nothing). on the laptop it measured 98 to 191 ms (run 41927796271);
  - an elevated third-party installer closing the service with UAC on, and what Restart Manager starts
    again (#251; ADR-0007);
  - a narrated session with the service log in view, to see whether the processor limit trips
    (`docs/dev/performance.md`, "Owner step on the laptop"), and the loop harness with real clicks
    (`docs/dev/e2e.md`);
  - a look at the new UI where CI does not render it (dropdowns, tool tips, hover and pressed states,
    the typed-confirmation dialogs, the enlarged frame).
- [ ] **The Narrator listening pass and an Accessibility Insights FastPass report** (ST-084): the
      checklist is `docs/ux/a11y-audit.md`, "Needs a person".
- [ ] **A Google OAuth client id, and one sign-in with a real Google account under the CSP** (ST-099):
      `docs/dev/web-admin.md`, section 1, and ADR-0009's "Owner steps".
- [ ] **Choose a host and a domain name** (ST-007). `docs/dev/deploy.md` takes any Linux host with
      Docker from there to HTTPS. PostgreSQL on a laptop is fine for M1 and the demo.
- [ ] **Choose a mail provider** (ST-007, ST-010) for invites and the offboarding receipt; until then
      codes are handed on by hand.
- [ ] **Get a code-signing certificate** for the installer (ST-112): Azure Artifact Signing is
      recommended, and `docs/dev/installer.md` says how to switch it on. Read ADR-0007 (Proposed) first;
      it lists the decisions it needs.
- [ ] **Set up auto-update** (ST-113), once signing and hosting exist: the update signing key in Azure
      Key Vault, its public key and the feed and package hosts in `shared/release/update-trust.json` by
      pull request, the `signing` environment's update settings, and the `release-stable` environment
      with you as its required reviewer. ADR-0008's "Owner steps" and `docs/dev/installer.md`, "Updates",
      list each one. Then check Windows' install prompt on the laptop with the first signed build.
- [ ] **A pilot MSP and technicians:** the baseline interviews (ST-110, with the market scan's §6
      question in the same conversations), five technicians for the usability round (ST-017), two
      sessions on the Review wireframe (ST-014), and the pilot itself (ST-116).
- [ ] **A ConnectWise sandbox or API member** for the real check of ST-091 to ST-094
      (`docs/integrations/connectwise.md`). The demo PSA does not replace it.
- [ ] **A Hudu API key** for the real check of ST-095 to ST-097 (`docs/integrations/hudu.md`).
- [ ] **Enable billing on the Gemini key.** The free tier (20 requests a day, 5 a minute) is enough for
      M1 and the demo, not for the two measurements ST-063 still owes: the thinking-token budget and the
      repair rate.
- [ ] **Measure Claude once** with its key: `SCREENTAIL_LIVE_LLM=1` runs the live drafting test, for the
      time and cost of a heavy session (ST-063).
- [ ] **A lawyer's read** of `docs/legal/consent-guidance.md`, the pilot agreement and the privacy
      policy (ST-111).
- [ ] **Have an MSP security lead read the privacy pack** (ST-114 AC1). It is unreviewed and says so.
- [ ] **Record the ten-minute narration** for ST-027's WER number (`research/fixtures/audio/README.md`).
- [ ] **ScreenConnect trial and a second Windows machine** for ST-030's golden sessions and ST-001 AC2.

### Decisions waiting for the owner

Each has a default in place, fail-closed where it matters, and the pull request, ADR or document that
chose it says so.

- **The installer** (ADR-0007, Proposed): how an uninstall treats technicians who are not signed in
  (their store is left unreadable rather than deleted). Since #251: whether to refuse Restart Manager's
  close while a session is recording (keeps a recording whole, but those installers then leave their
  files for a reboot); whether the UI stays registered for restart (the default now; you may revert it);
  and whether WiX's CloseApplication should ask first. Since #250: upgrades from builds without the new
  release step can still end in "restart required" once.
- **Auto-update** (ADR-0008, Proposed): the update apply model and its defaults (stable channel, a check
  every 24 hours, no checks under local-only unless the admin allows it), and **no time limit on the
  install's hold**: while Windows Installer runs no session starts, however long the prompt is left
  open. ADR-0008's "Decisions for the owner" lists the rest.
- **The web admin** (ADR-0009, Proposed, "Decisions for the owner"). Decisions 1 to 16 are fail-closed
  defaults to confirm: signup only for addresses on an operator-set list (empty: nobody); 5 seats for a
  new tenant; one owner per tenant and one tenant per Google account; invite codes shown once (no mail
  yet); 8-hour sessions with no refresh; no credential shown on the web; no sign-in while Google's keys
  cannot be fetched; no Workspace-domain check; ten anonymous calls a minute per address; no proxy
  trusted until listed; a 10-minute sign-in nonce; inline styles in the page's CSP for Google's button.
  Decisions 17 to 20 are open: one rate budget or two for an office behind one address, how wide an
  IPv6 caller is, binding the nonce to the browser, and counts kept per instance. Decisions 21 and 22 are
  the invite code's length (20 characters) and the warning threshold (50 refused codes in 10 minutes);
  decision 23 is made (the old ten-character codes are no longer accepted).
- **The deployment** (`docs/dev/deploy.md`, section 10): the host and the name, staging or not,
  HSTS beyond one year, where backups go and who checks a restore, full IPv6, a non-superuser schema
  owner, TLS to PostgreSQL if it moves off the host, container memory and process limits, pinning the
  proxy build's two Alpine packages, and the assumption that the host runs nothing else.
- **Accessibility defaults** (`docs/ux/a11y-audit.md`, "Owner decisions"): `?` opens the shortcut sheet
  only outside a text field and the sheet is not modal; scroll bars stay 12 px; blurring a region still
  needs the mouse (a keyboard way would be a new ticket); History's headers are Tab stops; Review's pane
  gaps are 28 px; the sheet lists the shipped hotkeys. Also, the hardware workflow's comment that the
  laptop job is safe on a machine in use is yours to update, since editing that workflow turns on the
  hardware gate.
- **The microphone meter** (#244, ST-080): it opens the microphone outside a session while the card is
  looked at, so Windows' microphone light comes on; its five-minute cap is a chosen number; there is no
  meter during a paused session; a device change can take effect within a session.
- **The idle hooks** (#252, ST-031): the name hook's refusal count is now shown in "What's being
  captured right now?" as "Hook fallbacks" (contract v8; confirm the wording and that a count with no
  alert is enough); the focus hook comes back with the session rather than staying in all day; while a
  classic console is in front the name hook hears every thread, at the old idle cost.
- **Pause wording** (#211): the pill and tray read "Paused: ..." with a colon, where the spec has a dash.
  Confirm, or give the wording.
- **Idle sessions in a remote tool** (#216): a session runs for as long as its remote tool stays in
  front; before #216 it was stopped after the grace. Should there be an input-idle limit?
- **How elevation is judged** (#216): a window is elevated when its integrity level is above the
  service's own. Should the service also check its own token's elevation type, so the answer is the same
  however it was started? (Since #251 a fully elevated service refuses to start at all.)
- **Waiting for the first policy** (#213): drafts, publishes and metrics wait until the first policy
  fetch with a device token has landed. Confirm.
- **An outbox with no backend** (#215): with no backend configured, a queued item is failed after a day
  and can be retried by hand, rather than waiting quietly. Confirm, or choose waiting.
- **A publish that may have landed** (ST-090, ST-093): since 2026-11-08 an `unknown` or `timed_out` row, and
  a connection to the service that dropped mid-publish, is "not confirmed" and Retry is held behind an "I
  checked the ticket" checkbox (the owner accepted the "confirmed" choice on #265, with O2 to O4: the
  service remembers it across restarts, checks each destination sent against what it holds, and a
  reopened Review offers each destination as the last publish left it). Still open: a time budget for the
  whole publish, and an "it's there, mark it published" action, without which an unconfirmed destination
  stays held until the technician ticks the box and publishes it again.
- **The local text model** (ST-065): llama.cpp and a model download on the technician's machine.
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
- **Frames still waiting for redaction when Windows closes the service are not counted** (#251). They
  wait only in memory (ADR-0006), so they go with the process and are never stored unredacted.
- **The deployment is IPv4 only** (#249): with an IPv4-only edge network every IPv6 client would share
  one rate-limit count, so the guide says to publish no AAAA record until full IPv6 is set up.
- **Not done from the 2026-09-20 review, with reasons:** the hardware workflow as a reusable workflow
  (a laptop that is off would hang the run for a day instead of failing in 35 minutes; revisit if the
  repository goes private); a model hash cache (weakens the one check between a corrupt download and
  native code); the SQLite `cache_size` pragma (needs a laptop measurement, not reasoning);
  `run-local.ps1` deleting its output directory without asking (the directory is documented as
  disposable).
- **ST-006's frames are drawn, not captured.** Fine for UI and pattern work, useless for judging OCR.
- **A throttled session can go without scene frames for long stretches** (#232): speech is never
  throttled, so steady narration can hold the service over budget. Clicks and marked moments are still
  photographed, and ST-026's "frame within 2 s" is suspended while throttled (`docs/dev/performance.md`).

## 7. Next steps

| When | What | Who |
|---|---|---|
| **Now** | Set the demo laptop up again from `main`, then **M1**: one real session, one real note, with the demo scripts | Owner |
| Right after | Write down what the first note got right and wrong, with the rehearsal checklist; that is the first row of the eval corpus, before ST-030's ten sessions exist. The laptop hand checks in §5 can be done in the same sitting | Owner, ten minutes for the note |
| Then | Fix what M1 finds. Until then, small work that needs no owner: making the loop harness a required check (it has passed on `main`'s last five runs), and ST-013's golden replay once ST-030 exists | Agent |
| Phase C proper | The real ConnectWise and Hudu checks (ST-091 to ST-097) | Agent, once there is a ConnectWise sandbox or API member and a Hudu key |
| In parallel, when the inputs exist | ST-027's WER number (the recording), ST-030 and ST-062 (ScreenConnect and a second machine), ST-063's two Gemini measurements (billing) and Claude's (its key) | Owner supplies; agent runs |
| Phase D | Hosting (ST-007: `deploy/` is built and waits for a host and a name), the web admin's first real sign-in (ST-099), signing the installer (ST-112) and switching on auto-update (ST-113), the Narrator pass (ST-084), the security-lead review, the legal read, the usability round, the pilot | Both |

**Why this order.** The product has never drafted a note from a real session. M1 is the one thing that
tells us whether the next month goes into the pilot or into fixing what the first note got wrong, and
the demo scripts make it a short sitting at the laptop. Everything after it either needs the owner's
accounts and people or should wait for what M1 shows.

### Needs a decision or an account

ST-007 needs a host, a domain name and a mail provider (and, for its cloud half, a cloud account).
ST-099 needs a Google OAuth client id. ST-112 needs a code-signing certificate. ST-113 needs the update
signing key in Key Vault, the hosting and the `release-stable` environment. ST-091 to ST-094 need a
ConnectWise sandbox or API member, and ST-095 to ST-097 a Hudu key. ST-063's measurements need Gemini
billing and the Claude key. ST-111 needs a lawyer. ST-017, ST-110, ST-114's review and ST-116 need
technicians, a pilot MSP and a security lead.

## 8. Handy commands

| Want to... | Do |
|---|---|
| Run client tests | `dotnet test client/ScreenTail.sln` |
| Run backend tests | `dotnet test backend/ScreenTail.Backend.sln` (SQLite; never calls a model) |
| Run web tests | `cd web && npm ci && npm test` |
| Run research tests | `cd research && pytest` |
| Check the deployment stack on a machine with Docker | `./deploy/smoke.sh` (what CI's Backend job runs; `docs/dev/deploy.md`, "How this is checked") |
| Deploy to a Linux host | `docs/dev/deploy.md`, from section 1 |
| Set up the demo on the laptop (once, and again after #248) | `scripts\windows\demo\setup.cmd` |
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
