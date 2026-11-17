# ScreenTrail: project standing

**Snapshot taken:** 2026-11-17, of `main` at `480d53f9` (#334). It covers every merge since the
2026-11-10 snapshot (#272, #274 to #316 and #321 to #336, the last of them on 2026-11-17): M1 on the demo
laptop and the fixes it asked for, the Outbox and Iris redesign and the design pass after it, a round of
redaction recall, the 2026-11-15 security review and its fixes, the dependency clean-up, the capture
service's processor cost measured, and the CI work that keeps the one spare laptop from holding up every
pull request.
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next.
**Source of truth:** the **Status** line on each ticket in `Build Plan/02-Backlog-v0.4.md`. This page summarises; the backlog decides. `docs/README.md` says which document is which.

---

## 1. Where things stand

**M1 is done** (2026-11-13, on the demo laptop, at `main` `074d4cfb`, #311; recorded by #315). AnyDesk
taking focus started a session by itself; about six minutes later the bundle (13 of 15 screenshots, 26
transcript segments, about 19k tokens, nothing purged unredacted) went to the backend, Claude
(`claude-opus-5-5`) answered in 17.5 s, and the owner read the note in Review: **the notes were good**,
and the pill and the tray showed the recording the whole time. Publish was not part of it (the demo PSA
was off). What it found:

- **The capture service went over its processor budget**, so the throttle gave up scene sampling and
  most OCR for most of the session. M1 read about 25% of the machine; that was almost certainly one of
  the throttle's one-second readings while whisper held 2 of the 8 logical processors. The budget is 60%
  of one core, which is 7.5% of that laptop, not 15%. Speech was about 80% of a narrated session. #334
  cut it, and it still trips the throttle; a fix that does not is built and off until the owner's ST-027
  recording (below).
- **A quick focus on AnyDesk left an empty 18-second session.** Fixed by #338 (ST-023): a session with
  nothing in it is discarded when it ends, with a quiet row in History, instead of drafted.
- The dark theme was hard to read and some screens waste space: #313 (dark layers), with #312
  (Appearance) and #336 (light mode everywhere, compact density; below).
- Drafted titles carried em dashes because `note_v1` asked for them: fixed by `note_v2` (#314).

**What is proven now, and what is not.** A real session, the real backend, the real model and a person
reading the note have run together once, and the note was good. A publish from a real session has not:
not to the demo PSA, and not to ConnectWise or Hudu, which have still never been called for real.

**The design pass is on `main` (#336, ST-016).** #317 merged on 2026-11-17 into its stacked base,
`st-016-no-em-dashes`, instead of `main`; #336 is the same content against `main`. Light mode is now light
across the whole window, the rail, the title bar and the pill included, as the owner asked on
2026-11-13, with compact density and Windows 11 style Settings tiles (spec v0.7.0). It also fixes the
tray icon's teardown from another thread, which now runs on the UI thread. The owner has not looked at it
on the laptop yet (§5).

**The capture service's processor cost is measured (#334, ST-031).** The 4.7% CI recorded for recording
(#223) was of one core, 0.6% of the machine, with no speech model loaded and a tiny window, so it never
said a narrated session would fit. Whisper now runs on one thread: a narrated session went from 105.2% to
95.9% of one core (13.1% to 12.0% of the machine; the peak one-second reading from 33.8% to 21%). It
still trips the throttle. The demo now publishes Release, the cost test fills the screen, and a narrated
recording is held under 120% of one core. A segment-sized encoder context ships off behind
`SCREENTRAIL_SPEECH_SIZED_CONTEXT=1`: with it on, a narrated session is 42% of one core (5.3% of the
machine) and the throttle never trips. Word error is unchanged on synthetic speech only, so the default
waits for the owner's ST-027 recording. Known follow-up: on one thread, a stop in the middle of a
transcription routinely passes its 500 ms bound and the release finishes in the background, so the next
test's memory reading is unreliable.

**Pull `main` before the next run on the laptop.** The pipe contract is version 12 (v10 for "Not
confirmed", v11 for the rail's rows, v12 for the diagnostics' backend note), and the service and the UI
must match it exactly. The demo scripts are unaffected by #326's machine-wide backend address: they run a
development build from the clone, which still reads `SCREENTRAIL_BACKEND`.

**What landed since 2026-11-10:**

- **The fixes M1 asked for** (#312 to #316). Settings has an Appearance card (#312, ST-070): *Use my
  Windows setting* (the default, followed live), Light or Dark; before it the app always started dark.
  The dark theme's frame, page and cards are told apart and its text is lighter (#313). `note_v2`
  writes no em dashes and titles as "symptom: cause" (#314, ST-061); `note_v1` is kept for the notes it
  wrote. The owner then asked for unnecessary em dashes to go everywhere: 1,185 lines had one, 80 are
  left, each on purpose (real window titles the code matches, and `.github/`) (#316).
- **The Outbox and Iris redesign, done 2026-11-13** (#284 to #311, ST-016, spec v0.6.0 to v0.6.12). The
  owner chose the Iris brand (`#6A43C4`) and the "Outbox" layout on 2026-11-11. Tokens v0.6.0 and spec
  v0.6.0 (#284); the app and per-state tray icons (#285); Review as an envelope over the note, a receipt
  after publishing (#287), and the note edited in place as it will land on the ticket (#296, #303); the
  pill with Mark, Pause and Stop and an expanded card with the last three events (#289, ST-072);
  Settings as one page with one Save bar (#290); History as an inbox rail beside the session pane
  (#292), with rows by company, title and ticket (#305, contract v11, migration 0015) and the All
  sessions table behind it (#301, #308); the setup wizard (#302); the tray menu to Spec S1 (#306,
  ST-071); the diagnostics panel, one confirm dialog for every question and the shortcut sheet (#307,
  #310); and the last polish (#311). Behind it, a copy and state pass (#283), 3:1 input edges and a style
  lint (#281), a render harness with real high-contrast themes, 150% and focus states (#282, #286), and a
  ratchet on literal colours and sizes in the views (#295). Every one of these screens is rendered in CI
  in four themes; on a real screen, only M1's Review and the owner's look at the light theme so far.
- **"Not confirmed" outside Review, and Mark as published** (#274, ST-093, contract v10). History shows
  a "Not confirmed" badge beside a session whose publish may have landed, the tray counts them between
  sessions, and History's Mark as published settles one on the technician's word, sending nothing.
- **Redaction recall, measured and raised** (#275, #278, ST-042). Secrets that wrap onto a second row
  are masked, and the rules hold at 125% and 150% display scaling and through a scaled, lossy viewer
  (#275, which also carries the two fixes from the closed draft #269). Then #278 found where recall was
  lost (almost all of it in OCR reading the text, not in the rules) and reads every frame at twice its
  size, with rules for misread key prefixes, random-looking runs and "pass" without its colon. On the
  hosted runner's synthetic pages, secrets masked whole went from 73.3% to 97.2% at display scales (1,166
  of 1,200) and from 52.1% to 82.9% through the viewer (1,990 of 2,400). Costs: more of an ordinary page
  is masked (prose 1.3% to 4.5% of the page), and on the laptop a dense full HD frame took 1,465 ms to
  read at twice its size against a 700 ms budget that only the staged-frame test enforces (it held, with
  a median of 222 ms). ST-042's 98% gate on labelled frames is unchanged and still needs ST-030.
- **One question at a time, and no dialog during a capture** (#279, #280, ST-084). A second invoke of any
  command that asks, discards or sends does nothing while the first is up. Review's Discard, History's
  bulk discard and Delete everything ask nothing while any session is being captured, and ask again
  once the confirmation is typed.
- **Two product bugs the laptop found.** A session whose events reached the store out of time order would
  not open in Review, publish or export; events are now read in time order (#297). The window-rename hook
  could be left on a window no longer in front; it now follows the window in front (#299, ST-031).
- **The installer** (ST-112). A newer Visual C++ runtime that wanted a restart made a silent setup exit
  1641 without installing ScreenTrail, and could restart the machine when `/norestart` was left off; the
  runtime's restart now counts as success and ScreenTrail installs (#276). The runtime is pinned to
  14.52.37029.0 by URL, hash and version (#277). The setup program passes `BACKENDURL` on to the package
  (#330).
- **CI and the laptop** (ST-018). The laptop runs only for pull requests that touch hardware tests, and
  everything runs on it nightly (#298); the hardware gate waits for a queued laptop instead of failing,
  and re-runs itself when the laptop passes (#294); a pull request's changes are read from its merge
  commit (#300); a laptop job stops at once on battery or under 30% charge (#304), after a readiness
  block that warns when the laptop looks slow (#288); the Windows client job has 30 minutes (#309); a
  screenshot test waits for the window's paint to reach the screen (#291); and the stop-time test names
  what covers its window when its frames miss the window's words (#293).
- **The security run** (#321 to #327, with #330 to #333; §1, "Security" below).
- **Dependencies** (#321, #328, #329): every Dependabot alert is closed.
- **Docs:** #272 was the 2026-11-10 snapshot and #315 the M1 note at its top.

### Security

On 2026-11-15 an internal review of `main` found no Critical issue, no authentication bypass and no
cross-tenant access. Its findings were fixed in #321 to #327, merged 2026-11-16, with follow-ups #330 to
#333. The public summary, area by area, is **`docs/security/2026-11-hardening.md`**; the review itself
stays private. In short:

- **Integration credentials are owner-only** (#327, ST-009, ST-082, ST-099; the owner's decision). The
  owner connects, replaces, tests and disconnects ConnectWise and Hudu in the web admin, which also lists
  every company mapping and the last credential and mapping changes with who made them. A technician's
  Settings, Integrations is read-only and keeps Test connection and the mapping card. A sealed secret is
  bound to its tenant, provider and site; legacy rows are upgraded at startup. Every text a technician
  reads now says "your ScreenTrail owner", and none sends them to Settings to fix a credential or names a
  server setting (#331, #333).
- **Publish and provider inputs are bounded** (#322): two publishes in flight per device and ten per
  tenant, refused with a 429 before the body is read; size limits on every part of a note; Hudu's company
  list capped; the device token exchange limited to 16 KB and to tokens of the issued shape; the
  "Reviewed by" line from the signed-in technician; ConnectWise ticket ids as numbers; a memory ceiling on
  the API container (`SCREENTRAIL_API_MEMORY`, 2 GB by default).
- **A locked local-only holds without the device's say-so** (#326, contract v12): the backend refuses to
  draft for such a tenant (`403 local_only`) and the client drafts on the device; an installed build reads
  its backend address only from `HKLM\SOFTWARE\ScreenTrail\BackendUrl`, written by `BACKENDURL`; device
  sign-in has its own egress purpose, and under local-only the background renewal sends nothing.
- **The pipe** (#325, ST-012): in a signed build each end requires the other to run from its own folder,
  and the service claims the pipe name with its first instance.
- **Capture privacy** (#324): clicks where no frame may be taken keep no position; title-based exclusions
  are described as such; see-through windows are not photographed; a retitled window is judged on its new
  title before the next frame.
- **Updates** (#323, ST-113): one signing key per channel, a key-and-subject publisher match when there
  is no identity-validation usage, and the install-folder check on uninstall and repair too.
- **CI and the laptop runner** (#321): `-NoProfile` and pinned actions in the laptop jobs, a skipped
  laptop job counted as no evidence, the e2e backend kept away from the demo's secrets, and no settings
  or secret files in the backend image.
- **PSA company names are checked against the PSA** (#332, ST-097): a mapping is saved only for a
  company the tenant's PSA has, under the PSA's own spelling. The ConnectWise query syntax still has to be
  seen working in the sandbox check.
- **Dependencies:** the 83 Dependabot alerts opened on 2026-11-16, all in development and build tools or
  the fixture renderer's Pillow, were closed the same day (#328, #321, #329). None is open.

The owner's part of it is in §5.

**In progress, not on `main`.** No pull request is open at this snapshot.

**The laptop runner** was online when this snapshot was taken. Its last runs on pull request heads
passed (#317's in 43980716975, #334's final head in 44031237988). The nightly run on `main` passed on
2026-11-15 and on 2026-11-16 stopped at its first step because the laptop was on battery (#304's rule).

**The loop runs end to end on a Windows VM, unattended** (#230, ST-013). `scripts/windows/e2e/` takes one
session through to the note on the demo PSA's ticket with a scripted drafting provider (`docs/dev/e2e.md`).
It passed on every `main` run since 2026-11-10 that ran it (51 of 51; 20 of 20 steps in run 43999003447).
It is a required check (one of `ci-ok`'s needs) since 2026-11-17; a passing laptop run with real
hotkeys and clicks is still owed.

**Still true from the last snapshot.** The web admin is built, hardened, and off until a Google OAuth
client id is set (#245, #246, ADR-0009 Proposed). Invite codes are twenty characters (#248, #260). A
deployment exists for any Linux host with Docker, and no host is chosen (#249, `docs/dev/deploy.md`).
The installer waits for other programs' open files, and the service closes when Windows asks and refuses
to start elevated (#247, #250, #251); signing still waits for the certificate. The microphone is chosen in
Settings with a live level (#244), not yet tried with a real headset. A stop never waits for a
transcription in flight (#262, #263). A publish that may have landed is held until the ticket is checked
(#265). Keyboard blur (#257). The web admin's browser tests (#255, #259). A demo PSA stands in for
ConnectWise in Development (#218), and the backend tests drive the real ConnectWise and Hudu providers
against stand-in servers (#226). Auto-update is built and switched off (#233, ADR-0008 Proposed). The
processor limit is built (#232, ST-031 AC2) and tripped on M1's real session and on #334's narrated
laptop runs. The
2026-10-12 review is worked through (#173 to #216, #220).

**Of 85 tickets, 67 are Done or built, 9 are Partial and 9 are Open** (recounted from the Status lines
on 2026-11-16). The numbers are the same as on 2026-11-10, and no ticket changed group: this stretch
redesigned, hardened and measured tickets that were already built. The Status lines that moved say so
inside their group: ST-009, ST-082 and ST-099 (owner-only credentials), ST-097 (the PSA company check),
ST-113 (one key per channel), ST-061 (`note_v2`), ST-070 (Appearance), ST-031 (#334's measurement), and
the redesign's tickets
ST-016, ST-071, ST-074, ST-078, ST-079, ST-081 and ST-083. ST-042, ST-072 and ST-112 were brought up
to date by #272.

- **45 are done outright.**
- **22 are built with a named part still owed:** ST-099 (the web admin) owes a sign-in with a real
  Google account and the owner's OAuth client id, and a host for `deploy/`; ST-091, ST-092, ST-095 and
  ST-096 are done against recorded shapes and owe the real ConnectWise and Hudu checks; ST-093, ST-094
  and ST-097 are done on the server side (ST-093's outbox-key idempotency is still not built; ST-097's
  ConnectWise lookup is to be seen in the sandbox); ST-073, ST-076, ST-077, ST-078 and ST-085 are done on
  the Mac side; ST-080, ST-081, ST-082 and ST-083 are built and not yet seen on a real screen; ST-010
  (the mail provider and three ADR-0009 decisions), ST-062 (the corpus), ST-065 (the local model),
  ST-084 (the Narrator listening pass and a FastPass report) and ST-115 (round two) each owe their second
  half.
- **9 are Partial,** and each says what it waits for: ST-007 (the host, the name, the mail provider and
  the cloud half), ST-013 (a passing laptop run with real clicks, making the loop job required, the
  golden replay, resource figures and the nightly schedule), ST-014 (two technician sessions), ST-027
  (the WER number needs a human recording), ST-031 (the golden replay, the throttle's laptop sighting,
  the nightly harness, memory with speech and live figures in "What's being captured"; narration still
  trips the throttle as shipped, and the sized encoder context waits for ST-027's recording), ST-042
  (the recall gate needs ST-030's labelled frames), ST-112 (signing needs the certificate; an elevated installer closing the service is to be seen on the laptop), ST-113
  (auto-update is switched off until the owner creates the two keys, the hosting and the release
  environment), ST-114 (the security-lead review).
- **9 are Open:** ST-017 (the usability round), ST-030 (golden sessions), ST-110, ST-111 and ST-116 (the
  pilot), and the four v1.1 and v1.2 tickets ST-120 to ST-123.

ST-042's Status line now records #275 and #278 (it said #269's fixes were not merged; they landed in
#275). ST-031's records M1's figure since #334.

Phases A and B of the ordered plan (backlog Part C) are complete apart from what needs the owner: ST-030
(golden sessions; a ScreenConnect trial and a second machine) and ST-062's corpus, which is ST-030's.

## 2. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (**public**) | https://github.com/hiratinspace/ScreenTrail | `main` green at #334 (`480d53f9`, CI run 44116130950); no pull request open on 2026-11-17. Secret scanning, push protection and Dependabot security updates on; no Dependabot alert open |
| Build plan | `Build Plan/` | Spec at **v0.7.0** on `main` (#336). v0.4.3 is accepted, and v0.6.0's brand and layout are the owner's choice; the amendment rows from v0.5.0 on mostly say **Awaiting owner review**. Backlog Part C is the ordered plan, Part D the amendments |
| Client | `client/` | `.Shared` (schema and IPC, contract version 12), `.Core`, `.Platform` (ADR-0005), `.Service`, `.UI`, and `.Tests.AxeRunner`. **4,375 tests** in the suite that runs anywhere and **592** in the Windows-only suite (CI run 44116130950 on `480d53f9`; ten of the 592 skipped on the hosted runner by design, which is the skip budget). Store migrations to 0015 |
| Backend | `backend/` | ASP.NET Core API: tenants, devices, device tokens, invites, policy, summarize (which now refuses a tenant that locks local-only) and publish (bounded and gated per device and tenant), the Gemini and Claude providers, the cost ledger, the credential vault (rows bound to tenant, provider and site), ConnectWise and Hudu clients with the PSA company lookup, the demo PSA, the web admin's routes including the owner's integrations and their change record. **1,596 tests** (two skipped; CI run 44026084042 on `a862b128`, the last `main` run that ran the backend job). Runs locally on PostgreSQL; not deployed (ST-007) |
| Deployment | `deploy/`, `docs/dev/deploy.md` | Docker Compose for one Linux host: Caddy with automatic HTTPS, the API (memory ceiling `SCREENTRAIL_API_MEMORY`, 2 GB by default), PostgreSQL and the web admin from one origin. Checked by `deploy/smoke.sh` in CI's Backend job; no host or name yet |
| Research | `research/` | Fixtures, note prompts v1 (kept) and **v2** (current) with their hardening cases, the shared validator rules, the WER script and the draft-quality harness. **255 tests** (CI run 43930259036 on `3626322f`, #328, the last change to `research/`) |
| Web admin | `web/`, `docs/dev/web-admin.md` | Google sign-in, first-run tenant, Technicians and seats, Policy, **Integrations** (connect, replace, test, disconnect, mappings, recent changes; #327), Download client. **65 unit tests** and **5 browser tests** (CI run 43999003447). Lighthouse accessibility 100 as measured by hand for #245 and #246; not in CI and not rerun since |
| Shared contracts | `shared/` | The session schema (a click's `x` and `y` optional since #324), the design tokens (v0.6.0 Iris), the brand files (`shared/design/brand/`), the release trust file (each key with its channel, still empty), and the wire contracts (summarize, now with `local_only`; publish; session metric; tenant policy; invite code) |
| Demo setup | `scripts/windows/demo/`, `docs/dev/demo.md` | Setup, start, stop and the diagnostic report for the whole loop on the laptop. M1 ran on it on 2026-11-13 |
| Loop harness | `scripts/windows/e2e/`, `docs/dev/e2e.md` | One session through the whole loop with a scripted drafting provider. 51 of 51 `main` runs since 2026-11-10 passed (20 of 20 steps in run 43999003447); a required check since 2026-11-17 |
| Installer and updates | `installer/`, `docs/dev/installer.md` | The MSI and setup program, smoke-tested unsigned in CI: install, upgrades with a held file, Restart Manager, uninstall, `BACKENDURL` through both, an uninstall naming another folder; **220 of 220 checks** (one note) in CI run 43999003447. The Visual C++ runtime pinned (#277). The signed update channel with one key per channel (#323). Signing and updates stay off until the owner sets them up |
| Accessibility suite | `client/ScreenTrail.Tests.Windows/Accessibility`, `docs/ux/a11y-audit.md` | Every technician-facing screen, on the hosted runner and the laptop: 43 Accessibility Insights scans in CI run 43999003447. The Narrator pass is the part left for a person |
| Windows test loop | Hosted Windows VM and the spare laptop `SCREENTRAIL` | The hosted VM runs on every PR that touches the client. The laptop runs only for PRs that touch hardware tests, and nightly on `main` (#298); its jobs stop at once on battery (#304), and the gate waits up to five hours for a queued laptop (#294). Online on 2026-11-16 (`docs/dev/windows-test-loop.md`) |
| Branch protection | Ruleset "main" | **On.** PR required, `ci-ok` must be green, no force-push, no deletion. Three GitHub settings keep strangers' code off the laptop (`docs/dev/windows-test-loop.md`, Security) |

## 3. What the code does today

- The capture service starts per user, serves an authenticated named pipe (user ACL, verified client
  executable, per-run token in a file only that user can read), recovers what a crash left behind, and
  runs retention hourly. **Since #325** the service claims the pipe name with its first instance and
  checks every later instance has the same owner and DACL; a name already held is retried every second,
  logged, and shown in the UI as "Capture service not running". In a signed build each end also requires
  the other to run from its own folder, both paths read from the kernel. After a server that fails
  verification the UI asks again every 30 seconds.
- **Both processes check themselves at every start** (#251): elevated, another user or session 0 writes
  a fixed sentence to the Application log and exits (INV-6). **The service closes when Windows asks**
  (#251), inside 4 s, and a transcription in flight is told to abort (#262, #263).
- **The UI is the Outbox design** (spec v0.6.0 to v0.6.12). The window is a rail and a session pane. The
  rail is History: search (`Ctrl+F`), groups To check, Not confirmed, Published today and Earlier, rows
  headed by company, note title and ticket where known (contract v11), and All sessions with filters and
  bulk discard behind it. The session pane is Review: the envelope (To, As, Time, Also, one filled
  Publish with its reason beside it) over the note as it will land, edited in place, each step with its
  own frame; after publishing, the receipt. Settings is one scrolling page with a section nav and one Save
  bar; it gained Appearance (#312). The pill carries Mark, Pause and Stop and an expanded card with the
  last three events (#289). Every question the client asks goes through one dialog whose buttons say what
  happens (#307), one at a time (#279), and none is asked while a session is being captured (#280). Light
  mode is light across the whole window, the rail, the title bar and the pill included, with compact
  density and Settings as Windows 11 style tiles (#336, spec v0.7.0).
- **The UI is connected** (ST-085): tray, pill and diagnostics show live state; the UI tells the service
  every two seconds that the pill is on screen and where, and the service suppresses capture when it has
  not heard for six seconds (INV-4). The recording clock counts in the UI between snapshots (#262), and a
  UI that missed a state change is reconnected (#263).
- **A publish that may have landed** is "not confirmed" per destination, held until the technician ticks
  "I checked ticket #..." (#265), shown in History and counted in the tray, and settled from History's
  Mark as published without sending again (#274).
- The session state machine owns the lifecycle. Sources can only write while recording (INV-6).
  Auto-start follows the remote tool in front, and the auto-stop grace runs from when the tool loses
  focus (#216). The window-rename hook follows the window in front (#299), and a retitle still settling is
  judged before the next frame, never more loosely than before (#324). A session's events are read in
  time order (#297).
- **Clicks where no frame may be taken keep no position** (#324): an excluded or out-of-scope window, no
  decision yet, or a window that is not the one judged. The store keeps no position for any click without
  a frame, on write and on read. "URL" exclusions are matched against the window title, and Settings says
  so. A window drawn see-through, or colour-keyed when it is not a registered remote tool, is not
  photographed and the skip is counted.
- **A captured frame never touches the disk unredacted.** It waits in a bounded in-memory queue (depth
  4), the redaction worker takes it from there, and only the redacted, downscaled result is stored
  (ADR-0006). A frame the OCR engine cannot read, or that arrives when the queue is full, is dropped and
  counted, and the count reaches the draft so it hedges (ADR-0004).
- **Redaction** finds SSNs, Luhn-valid cards, credential shapes, spoken passwords and tenant patterns.
  On a frame, a match takes every word on its row closer than a space, a password cue takes its rows
  (#268), and a secret that wraps onto the next row is masked on both (#275). **Since #278** OCR reads
  each frame at twice its size (held to the pixels of a full HD frame at 2x, so 2560x1440 is read at 1.5x
  and 4K as captured), and three rules catch what OCR misreads: key prefixes with the underscore or `l`
  read wrong, random-looking runs of 16 or more characters, and "pass" without its colon. Text and pixels
  are masked from the same list of words.
- **Speech works.** Whisper `base.en` on one thread since #334, loaded when the first session wants it; a
  voice-activity gate and a hallucination filter; what is said while capture is suppressed is dropped.
- **The drafting path is wired end to end**, and M1 ran it with Claude. Bundle, outbox, sender,
  `POST v1/sessions/summarize`, the model, the validator and the note in the store; one model call per
  session; nothing sent before the first policy fetch has landed. The prompt is `note_v2` (#314). **Since
  #326** the backend refuses to draft for a tenant whose policy locks local-only, and the client then
  drafts on the device and, for an hour, drafts queued sessions there without sending them.
- **The publish path is built end to end**: note, time entry and KB article from the envelope, with the
  company mapping prompt and the ticket pre-selected from the window. **Since #322** the backend takes two
  publishes per device and ten per tenant at once, refused with a 429 that leaves everything as it was,
  and bounds the note. **Since #332** a company mapping is saved only for a company the PSA has. None of
  it has met a real PSA; in Development the demo PSA stands in.
- **Integrations are the owner's** (#327): the web admin's Integrations page stores, replaces, tests and
  removes ConnectWise and Hudu; a device's attempt is refused with `403 owner_only`, and the service
  refuses an older UI's attempt itself, so a secret never leaves the machine. A technician sees status,
  site, the last four characters and Test connection.
- **An installed build learns its backend from the machine** (#326, #330): only from
  `HKLM\SOFTWARE\ScreenTrail\BackendUrl`, written by `BACKENDURL` on the setup program's or the MSI's
  command line and kept across upgrades. A development build still reads `SCREENTRAIL_BACKEND` first.
  Without an address the device card, activation and diagnostics say an administrator sets it.
- **Device activation** takes a twenty-character invite code only and claims the invite and the seat
  under a lock on the tenant's row (#248, #260).
- **A recording session watches its own processor use** (ST-031 AC2, #232) and, over budget for ten
  seconds, samples the screen less, then reads one frame at a time, then stops scene sampling. Every
  frame it does take is redacted exactly as before (INV-1). M1 saw it throttle a real session, and a
  narrated session as shipped still trips it (#334).
- **Updates are built, and off** (ST-113): a signed manifest checked against a compiled-in key that is
  trusted for one channel only (#323). The trust file is empty, so no build checks for updates yet.
- **Every HTTP call the service makes goes through the egress allowlist**, by purpose (INV-8):
  `Publish` goes; `Policy` goes; `DeviceAuth` (activation, renewal) goes only on behalf of a publish or
  the policy fetch under local-only, never on the renewal timer (#326); everything else is refused.
- The audit log is hash-chained with a head anchor. Every process logs through one scrubbing sink
  (ST-011). A crash leaves a stack-only report, and only when opted in.
- Design tokens generate WPF dictionaries and web CSS from one source (v0.6.0, Iris), and the gallery
  and every screen render in CI in dark, light, Night sky and Desert.

**Not built:** the local text model (ST-065); the cloud half of hosting (ST-007); mail for invites; the
coverage banner on All sessions (placed in the spec; nothing counts the uncaptured sessions yet, #305);
"Start capture" from the tray when idle (an IPC change, #306). **Built and switched off until the owner
sets them up:** the installer's signature (ST-112, ADR-0007), auto-update (ST-113, ADR-0008) and owner
sign-in to the web admin (ST-099, ADR-0009).

### Seen on real hardware, and not

**Seen:** M1 on the demo laptop (2026-11-13): a real AnyDesk session drafted by Claude and read in
Review, with the pill and tray showing the recording and the processor limit throttling. The
Windows-only hardware tests and the input-injection job run on the laptop for pull requests that touch
them and every night on `main`. #334's processor cost of a narrated session, stage by stage. #278's OCR
timing on the laptop (725 ms at 1x and 1,465 ms at 2x for a dense full HD frame). Before this stretch:
the idle hooks, the accessibility suite at a scaled display, the stop times, ST-001's spike, and the Gemini drafting call on a heavy bundle (2026-09-19).

**Not seen yet:**

- A publish from a real session: to the demo PSA, and to ConnectWise or Hudu. Both clients are tested
  against recorded shapes and stand-in servers only; #332's ConnectWise company lookup is written from the
  documentation.
- The redesigned screens looked at on Windows by a person beyond M1's Review: the rail, Settings, the
  wizard, the tray menu, dropdowns, tool tips, hover and pressed states, and Narrator on any of them.
- #336's light window: the owner's check of it on the laptop.
- The 2x OCR read and the new rules on a real display at 125% and 150%, and redaction through a real
  remote viewer's scaler and codec (#275 and #278 used GDI+ bilinear and JPEG).
- A signed install with the UI and the service connecting from `C:\Program Files\ScreenTrail` (#325), and
  an installed build reading `BackendUrl` on a real machine (#326; the hosted smoke test checks the
  value is written and kept).
- `scripts\windows\probe-layered-windows.ps1` on the laptop with the real remote tools open (#324).
- The recording clock with Narrator and NVDA running (#262); the "not confirmed" state against a real
  PSA; the blur box read by Narrator.
- Claude's cost on a heavy session (M1 gave its time, 17.5 s, not its cost).
- The microphone picker with a real headset (ST-080 AC3); an elevated third-party installer closing the
  service with UAC on (#251).
- A sign-in to the web admin with a real Google account, and the deployment on a real host.
- The speech model's word error rate on a real narration (ST-027), and redaction recall on real captures
  (ST-042).
- RDP opacity from a second machine (ST-001 AC2), the toasts on Windows (ST-073), an installed, signed
  build, and an update installed through Windows' prompt.

## 4. Decisions already made

- **.NET 10 LTS** (support for 8 ends November 2026).
- **OCR on the full-size frame**; only the display and upload copy is shrunk (ADR-0001 finding 2a). Since
  #278 the OCR copy is read at up to twice its size, within a pixel budget.
- **Spec v0.4.1 to v0.4.3** (Q1 to Q6, the contrast adjustments, three S3 wording changes): accepted
  2026-09-15 (Part D, D-6).
- **A draft reports unrounded active minutes**; the client applies the tenant's billing rounding.
- **2026-09-15 sequencing** (Part D, D-1 to D-3).
- **Model provider: Gemini Flash** (`gemini-3.6-flash`), the backend's default since 2026-09-19.
  **Claude** (`claude-opus-5-5`) was added on 2026-10-29 (#225), the demo's setup offers it first, and
  M1 drafted with it.
- **2026-09-22, four decisions from the efficiency review:** US only for now; Whisper `base.en` on every
  machine, two threads (one since #334, ST-031); the microphone follows the session; Gemini billing
  gates measurement, not M1.
- **Draft validator, option A** (2026-10-21, split 2026-10-24, #206).
- **INV-4 and the hidden pill** (2026-10-25, delegated by the owner).
- **Owners sign in to the web admin with Google** (2026-10-31; ADR-0009). Decisions 23 and 25 made
  2026-11-04.
- **A stop does not wait for a transcription in flight** (the owner, 2026-11-05; #262, #263).
- **A publish that may have landed needs the technician's word** (#265, 2026-11-08; O1 to O4 accepted).
- **A password on screen takes its row** (the owner, 2026-11-09; #268).
- **The brand is Iris, the layout "Outbox"** (the owner, 2026-11-11; #284, spec v0.6.0). Settings has one
  Save bar for the whole page (the owner, #290).
- **For open UX choices, pick the friendliest option, record it and keep going** (the owner,
  2026-11-12). The redesign and security pull requests record the choices they made this way.
- **No unnecessary em dashes anywhere**, the product, its sample data and its docs included (the owner,
  2026-11-13; #314, #316).
- **Light mode is the whole window**, the rail too (the owner, 2026-11-13). Built in #317 and landed on
  `main` by #336 (§1).
- **Integration credentials are owner-only, edited in the web admin**; technicians keep the mapping
  prompt, and every change is shown to the owner (the owner, 2026-11-15; #327). ADR-0009 decision 6 is
  superseded.
- **A refused publish is a 429, not a 503** (#322): the client reads a 5xx as "may have landed", which
  would hold the technician up for nothing.
- **"Your ScreenTrail owner"** in every text a technician reads, and no server setting named to a
  technician (#331, #333).
- **A laptop job on battery stops at once** with one sentence that says what to do (#304).
- **Six ADRs Accepted:** 0001 to 0006. **Three Proposed**, waiting for the owner: 0007 (the installer,
  amended for `BACKENDURL`), 0008 (auto-update) and 0009 (owner sign-in to the web admin). ADR-0003 has
  dated amendments from #325.
- **The repository is public on purpose**: hosted Windows minutes are free for public repositories.
  Three repository settings, not workflow files, are what keep a stranger's pull request off the laptop.

## 5. Open items for the owner

- [ ] **Record the ten-minute narration for ST-027** (`research/fixtures/audio/README.md`), then **decide
      the sized encoder context's default** (#334, ST-031): it keeps a narrated session inside the
      processor budget, and word error is so far unchanged on synthetic speech only. Only if it fails on
      real speech, decide whether speech should count against the screenshot budget.
- [ ] **Check the light window on the laptop** (#336): the rail, Settings and the title bar buttons.
- [ ] **Pull `main` on the demo laptop before the next run** (contract v12), then
      `scripts\windows\demo\start.cmd` from a plain PowerShell window.
- [ ] **Review the spec amendments marked "Awaiting owner review"** (v0.5.0 to v0.7.0 in
      `Build Plan/01-UX-Design-Spec.md` §9), and the Appearance default (§5, decisions below).
- [ ] **On the first deployment, the vault:** run the vault upgrade (`--upgrade-vault-rows`, or let the
      startup sweep do it), check it prints `upgraded 0` and `legacy rows left: 0` and the log has no
      `Vault sweep` warning, then set `Vault:AllowLegacyRows=false` and restart
      (`docs/security/key-rotation.md`). If a row is left, reconnect that integration first.
- [ ] **Connect ConnectWise and Hudu in the web admin's Integrations page.** Credentials are owner-only
      now; technicians' Settings show them read-only.
- [ ] **Install with `BACKENDURL=`** on the setup program's or `msiexec`'s command line, or write
      `HKLM\SOFTWARE\ScreenTrail\BackendUrl` with RMM or Group Policy (`docs/dev/installer.md`). A machine
      upgraded from a build that relied on `SCREENTRAIL_BACKEND` has no backend until it is set. Keep
      credentials out of the address: a verbose MSI log records it (#330).
- [ ] **Harden the laptop runner** (#321, `docs/dev/windows-test-loop.md`): a drafting key with its own
      spend cap rather than the demo's, a database credential that reaches only the test databases, and,
      better, the runner under a standard Windows account that holds none of the demo's secrets. Before
      loosening any of the three repository settings, take the runner offline; `HW_RUNNER=false` does
      not protect it.
- [ ] **Run `scripts\windows\probe-layered-windows.ps1` on the laptop** with ScreenConnect, mstsc,
      AnyDesk, TeamViewer, Splashtop and a browser open, and paste its output (#324; it prints no
      titles).
- [ ] **Hand checks on the laptop:** the microphone picker with a real headset and Win+L (ST-080 AC3);
      Narrator and NVDA during a recording (#262); an elevated third-party installer closing the service
      with UAC on (#251); the loop harness with real clicks (`docs/dev/e2e.md`); a look at the redesigned
      screens where CI does not render them (dropdowns, tool tips, hover and pressed states).
- [ ] **The Narrator listening pass and an Accessibility Insights FastPass report** (ST-084):
      `docs/ux/a11y-audit.md`, "Needs a person".
- [ ] **A Google OAuth client id, and one sign-in with a real Google account under the CSP** (ST-099).
- [ ] **Choose a host and a domain name** (ST-007). `docs/dev/deploy.md` takes any Linux host with
      Docker from there to HTTPS; set `SCREENTRAIL_API_MEMORY` if the host has more than 2 GB.
- [ ] **Choose a mail provider** (ST-007, ST-010) for invites and the offboarding receipt.
- [ ] **Get a code-signing certificate** for the installer (ST-112): Azure Artifact Signing is
      recommended (`docs/dev/installer.md`; ADR-0007 first).
- [ ] **Set up auto-update** (ST-113), once signing and hosting exist: **two** Key Vault keys (pilot,
      stable) and **two** app registrations, each sign-only on its own key; both public keys in
      `shared/release/update-trust.json` with their channels, by pull request; the `signing` and
      `release-stable` environments' settings (`docs/dev/installer.md`, "Updates"; ADR-0008).
- [ ] **A legal privacy policy and DPA, and a lawyer's read** of `docs/legal/consent-guidance.md` and the
      pilot agreement (ST-111).
- [ ] **Billing for drafting:** credits on the Anthropic account for Claude, and billing on the Gemini
      key for ST-063's two measurements (the free tier is 20 requests a day). Measure Claude's cost once
      with `SCREENTRAIL_LIVE_LLM=1` (ST-063).
- [ ] **A ConnectWise sandbox or API member** for the real check of ST-091 to ST-094, which now also has
      to confirm the company lookup's condition syntax from #332 (`docs/integrations/connectwise.md`).
      Optionally, confirm in the developer portal that the vendor clientId is a public identifier (#322).
- [ ] **A Hudu API key** for the real check of ST-095 to ST-097 (`docs/integrations/hudu.md`).
- [ ] **A pilot MSP and technicians:** ST-110, ST-017, ST-014 and ST-116.
- [ ] **Have an MSP security lead read the privacy pack** (ST-114 AC1). It is unreviewed and says so.
- [ ] **ScreenConnect trial and a second Windows machine** for ST-030's golden sessions and ST-001 AC2.

### Decisions waiting for the owner

Each has a default in place, fail-closed where it matters, and the pull request, ADR or document that
chose it says so.

- **The light window** (#336, built in #317): soft grey rail, compact density, Windows 11 style Settings
  tiles, the KB reason as a tooltip, and whether simple on/off settings should become switches (not
  done).
- **The sized encoder context** (#334): off by default until ST-027's real recording shows its word
  error; with it on, a narrated session stays inside the processor budget.
- **Appearance's default** (#312): it now follows Windows' app mode, where the project scope said dark
  by default. A one-line change if dark should stay the default.
- **The tray** (#306, spec v0.6.10): "Start capture" when idle (needs a start that reads the tool in
  front, an IPC change), a glyph for "service stopped" and a numbered draft badge (brand artwork).
- **The redesign's choices** recorded under "As built" in the spec, v0.6.1 to v0.6.15, all chosen by the
  friendliest-option rule and awaiting review. Two to note: the rail's rows carry the note's title and
  ticket (contract v11), and the pipe refuses any mixed UI and service versions.
- **Redaction's cost** (#268, #278): a password's rows are masked, the cue word included; reading at
  twice the size masks more of an ordinary page (prose 4.5%, tables 4.8% of the page on the probe's
  pages); a dense full HD frame costs about twice the 700 ms budget on the laptop; hex, base32 and other
  single-class runs with no cue are left readable, since masking them would take every GUID on a device
  table (`docs/security/data-flow.md`). Confirm, or say what should stay readable.
- **The security run's choices**, each recorded in its pull request: mappings matched automatically by
  name while publishing are not recorded, only a person's (#327); 30 mapping changes per device in ten
  minutes (#327); an installed build is one in the install folder or the validly signed service anywhere,
  with the publisher to be pinned once one ships (#326); after a `local_only` answer queued drafts are
  drafted on the device for an hour (#326); a store-certificate build renewed with a new key is installed
  by hand once (#323); a remote tool's own colour key is allowed (#324); the ConnectWise vendor clientId
  goes to tenant-chosen sites (#322).
- **The installer** (ADR-0007, Proposed): how an uninstall treats technicians who are not signed in;
  whether to refuse Restart Manager's close while recording; whether the UI stays registered for
  restart; whether WiX's CloseApplication should ask first. A setup that exits 0 no longer tells an RMM
  tool that the runtime's update waits for a restart (#276).
- **Auto-update** (ADR-0008, Proposed): the apply model and its defaults, and no time limit on the
  install's hold.
- **The web admin** (ADR-0009, Proposed, "Decisions for the owner"): decisions 1 to 22 and 24 as listed
  there (decision 6 is superseded by #327; 23 and 25 are made).
- **The deployment** (`docs/dev/deploy.md`, section 10): the host and the name, staging, backups, IPv6,
  the schema owner, TLS to PostgreSQL, container limits.
- **Accessibility defaults, keyboard blur, the recording clock, the stop-time test, the demo's
  elevation check, the microphone meter, the idle hooks, pause wording, idle sessions in a remote tool,
  how elevation is judged, waiting for the first policy, an outbox with no backend, a publish that may
  have landed, the processor limit, a UI that misses a state change, Settings and unsaved edits, and
  diagnostics counts that can say "not known":** unchanged since the 2026-11-10 snapshot; each is in the
  pull request named there (#211 to #268) and in `docs/ux/a11y-audit.md` and `docs/dev/performance.md`.
- **The local text model** (ST-065): llama.cpp and a model download on the technician's machine.

## 6. Known gaps, deliberately left

- **ST-042 has no corpus recall gate**: it needs ST-030's staged captures. On synthetic pages recall is
  97.2% at display scales and 82.9% through a scaled lossy viewer (#278), short of 98%; most of the loss
  is OCR not reading the text at all, and a second reading of the whole frame would cost more than the
  budget.
- **What the redaction rules do not cover:** a run with no cue and only one class of character (hex,
  base32, capitals with digits); a mirrored layout with the value before a Latin label; a split between a
  quarter and 0.35 of a row's height at 125%, where the token rule has no margin for a pixel of rounding
  (#275). All recorded in `docs/security/data-flow.md`.
- **Capture privacy limits** (#324, `docs/security/threat-model.md`, "What is not mitigated"): a remote
  tool's own colour key can show a few pixels behind its shaped corners; per-pixel alpha windows, acrylic
  backdrops and window regions are allowed. Per-window capture is the full fix.
- **The pipe's folder rule** (#325, ADR-0003): a file symbolic link to an installed executable is
  recorded under the install folder's path; making one needs an administrator or Developer Mode.
- **A verbose MSI log records the backend address** (#330), which hidden properties do not cover.
- **A publish nobody could confirm stays a draft** (#265) until it is ticked, marked published from
  History, or discarded.
- **"No mic" is asked once when a session starts** (#262), so a microphone lost or plugged in during a
  session is not shown until the next one.
- **OCR is a best-effort input to redaction, not a guarantee** (ADR-0001 finding 9).
- **A 4K frame exceeds ST-025's budget and nothing fails**: about 309 ms against 120 ms on the laptop.
- **Each frame is decoded twice**, once to read it and once to paint on it (kept out of ADR-0006).
- **The full-disk case under-reports `frames_purged_unredacted`**, and frames waiting for redaction when
  Windows closes the service are not counted (#251); neither is ever stored unredacted.
- **The deployment is IPv4 only** (#249).
- **Not done from the 2026-09-20 review, with reasons:** the hardware workflow as a reusable workflow, a
  model hash cache, the SQLite `cache_size` pragma, `run-local.ps1` deleting its output directory.
- **ST-006's frames are drawn, not captured.** Fine for UI and pattern work, useless for judging OCR.
- **A throttled session can go without scene frames for long stretches** (#232): speech is never
  throttled, and M1 showed the service over budget for most of a real session. Clicks and marked moments
  are still photographed.

## 7. Next steps

| When | What | Who |
|---|---|---|
| **Now** | **ST-027's recorded narration**, then the sized encoder context's default (#334, ST-031): narration as shipped still trips the throttle, and the sized context, which does not, waits for word error on real speech | Owner records and decides; agent measures |
| Then | **The Outbox and Iris leftovers:** the coverage banner (not built), the manual Narrator pass, the tray's "Start capture" when idle, and the Settings copy issues found in the #290 gallery | Agent, with the owner's decisions |
| Small, no owner | A stop mid-transcription on one thread passing its 500 ms bound (#334) | Agent |
| Phase C proper | The real ConnectWise and Hudu checks (ST-091 to ST-097), with #332's company lookup | Agent, once there is a ConnectWise sandbox and a Hudu key |
| Go-live prerequisites | Hosting (ST-007), a code-signing certificate (ST-112), a legal privacy policy and DPA (ST-111), drafting billing, a ConnectWise sandbox, a Hudu key, the first deployment's vault upgrade, and the laptop runner's hardening (§5) | Owner |
| In parallel, when the inputs exist | ST-030 and ST-062, ST-063's measurements | Owner supplies; agent runs |
| Phase D | The web admin's first real sign-in (ST-099), auto-update (ST-113), the Narrator pass (ST-084), the security-lead review (ST-114), the legal read, the usability round, the pilot | Both |

**Why this order.** M1 answered the question the last snapshot asked: a real session gives a good note.
What it also showed is that on the demo laptop the capture service spends most of a session throttled,
taking fewer screenshots and reading fewer of them. Every frame it takes is still redacted, so this is
not a privacy gap, but it is fewer screenshots in the note, and it is the first thing a pilot technician
would meet. #334 found the cost (speech) and built the fix; what it waits for now is the owner's ST-027
recording, and the empty session is fixed (#338), so the agent's first items are the Outbox leftovers. The security run is merged and leaves only
owner steps; the redesign is merged, #317's part through #336. Everything after that needs the owner's
accounts, a PSA or people.

### Needs a decision or an account

ST-007 needs a host, a domain name and a mail provider (and, for its cloud half, a cloud account).
ST-099 needs a Google OAuth client id. ST-112 needs a code-signing certificate. ST-113 needs two update
signing keys in Key Vault, the hosting and the `release-stable` environment. ST-091 to ST-094 need a
ConnectWise sandbox or API member, and ST-095 to ST-097 a Hudu key. ST-063's measurements need Gemini
billing and the Claude key. ST-111 needs a lawyer. ST-017, ST-110, ST-114's review and ST-116 need
technicians, a pilot MSP and a security lead.

## 8. Handy commands

| Want to... | Do |
|---|---|
| Run client tests | `dotnet test client/ScreenTrail.sln` |
| Run backend tests | `dotnet test backend/ScreenTrail.Backend.sln` (SQLite; never calls a model) |
| Run web tests | `cd web && npm ci && npm test` |
| Run the web admin's browser tests | `cd web && npm run e2e` (needs the backend's PostgreSQL; `docs/dev/web-admin.md`) |
| Run research tests | `cd research && pytest` |
| Check the deployment stack on a machine with Docker | `./deploy/smoke.sh` (what CI's Backend job runs; `docs/dev/deploy.md`, "How this is checked") |
| Deploy to a Linux host | `docs/dev/deploy.md`, from section 1 |
| Upgrade the vault's legacy rows by hand | `dotnet run --project src/ScreenTrail.Api -- --upgrade-vault-rows` in `backend/` (`docs/security/key-rotation.md`) |
| Install for a tenant with its backend address | `ScreenTrail-Setup.exe BACKENDURL=https://<backend> /quiet /norestart` (`docs/dev/installer.md`) |
| Set up the demo on the laptop | `scripts\windows\demo\setup.cmd` |
| Start and stop the demo | `scripts\windows\demo\start.cmd`, then `scripts\windows\demo\stop.cmd` |
| Make one file to send when the demo is not working | `scripts\windows\demo\report.cmd`, before `stop.cmd` |
| Run one session through the whole loop, unattended | `scripts\windows\e2e\run-e2e.cmd` (`docs/dev/e2e.md`) |
| See which windows the capturer will not photograph | `powershell -ExecutionPolicy Bypass -File scripts\windows\probe-layered-windows.ps1` |
| Get the first real note by hand | `docs/dev/first-draft-end-to-end.md` |
| Run the product on the laptop without the demo scripts | `powershell -ExecutionPolicy Bypass -File .\scripts\windows\run-local.ps1` |
| See which secrets the backend has, without retyping them | `dotnet user-secrets list --project backend/src/ScreenTrail.Api` (prints values: mind the shoulder) |
| Render the component gallery | on Windows: `dotnet run --project client/ScreenTrail.UI -- --gallery` |
| Render the note pane or the HUD | on Windows: `dotnet run --project client/ScreenTrail.UI -- --note --screenshot <dir>` or `-- --hud --screenshot <dir>` |
| Read the security run's public summary | `docs/security/2026-11-hardening.md` |
| Read the closed weakness review | `docs/review/weaknesses.md`; the status block at the top says what is left |
| See the market and the idea list | `docs/product/2026-09-15-market-scan-and-ideas.md`: §2.4 for the wedge, §4 for ideas |
| Look at a fixture session | `research/fixtures/handcrafted/<name>/session.json` and its `frames/` |
| Regenerate schema or token outputs | `npm run codegen` in `shared/schema` or `shared/design` |
| Open the wireframes | `open docs/ux/wireframes/index.html` |
| Trigger a laptop spike run by hand | `gh workflow run spike-windows.yml --ref main` |
| See what the laptop measured | `gh run download <run-id>` then read the text files; images are never uploaded |
| Check the audit log verifies | `store.VerifyAuditAsync()`; the export says so too |
| Get the laptop ready for a CI run | `docs/dev/windows-test-loop.md`, "Before a laptop run" |
