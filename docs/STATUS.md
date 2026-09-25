# ScreenTail — project standing

**Snapshot taken:** 2026-09-22
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next.
**Source of truth:** the **Status** line on each ticket in `Build Plan/02-Backlog-v0.4.md`. This page summarises; the backlog decides. `docs/README.md` says which document is which.

---

## 1. Where things stand

**Everything between a real session and a real note is built, merged and green. None of it has run end
to end yet.** Capture, redaction, speech, the bundle, the outbox, the sender, the backend endpoint and the
model call each have tests; the whole chain has never been exercised with a real session, a real backend
and a real key at the same time. That first run is **M1**, and `docs/dev/first-draft-end-to-end.md` is
the step list for it.

**The Review and History panes are in the live shell as of 2026-09-25** (#138, #140). Until then the
shell window's content area showed the pane's *name*; the note editor and filmstrip were rendered only by
the screenshot harness, and no message on the pipe could carry a session to the UI. Now a draft that
becomes ready opens in Review over the pipe — the note, the screenshots, include, delete and blur — and
History lists sessions and opens one on a double-click. **Not yet seen on a real screen:** CI renders
both panes over the fixture in three themes; the first live run is M1. Settings is still a sentence
(ST-081) and the timeline row (ST-076) has a model and no view.

**Of 85 tickets, 42 are Done, 4 are Partial and 39 are Open.** Phases A and B of the ordered plan
(backlog Part C) are complete apart from the two that need the owner: ST-030 (golden sessions; needs a
ScreenConnect trial and a second machine) and ST-062 (the eval harness, which runs on ST-030's sessions).
The four Partial tickets each say what they wait for: ST-027 (the WER number needs a human recording),
ST-042 (the recall gate needs ST-030's labelled frames), ST-085 (done on the Mac side and green on the
laptop; the ticket's own Windows walk-through has not been done by a person), ST-114 (the security-lead
review).

**Two reviews have been worked through since the last snapshot.** The 2026-09-15 adversarial review
(`docs/review/weaknesses.md`) is closed: thirteen of fourteen findings fixed, one (P1-7) tracked under
ST-049. The 2026-09-20 efficiency and security review shipped as PRs #96–#132; what it deliberately did
not do is listed in §6.

## 2. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (**public**) | https://github.com/hiratinspace/ScreenTail | `main` green at #132, no open pull requests |
| Build plan | `Build Plan/` | Spec at **v0.4.3**. Backlog Part C is the ordered plan, Part D the amendments |
| Client | `client/` | `.Shared` (schema + IPC), `.Core` (platform-neutral logic), `.Platform` (Windows adapters, ADR-0005), `.Service`, `.UI`. **1,095 tests** on macOS, plus the Windows-only suite |
| Backend | `backend/` | ASP.NET Core API: tenants, devices, device tokens, the summarize endpoint, the Gemini provider, the cost ledger. **181 tests.** Runs locally on Postgres; not deployed (ST-007) |
| Research | `research/` | Fixtures, note prompt v1 with its hardening cases, the WER script. **130 tests** |
| Shared contracts | `shared/` | The session schema, the design tokens, and the summarize-request wire contract both sides test against |
| Windows test loop | Hosted Windows VM + spare laptop `SCREENTRAIL` | Both run on every PR that touches capture. The laptop's two jobs were green on the last PR that needed them (2026-09-22) |
| Branch protection | Ruleset "main" | **On.** PR required, `ci-ok` must be green, no force-push, no deletion. The repository is public; three GitHub settings keep strangers' code off the laptop (`docs/dev/windows-test-loop.md` § Security) |

## 3. What the code does today

- The capture service starts per user, serves an authenticated named pipe (user ACL, verified client
  executable, per-run token in a file only that user can read), recovers what a crash left behind, and
  runs retention hourly with a vacuum only when a third of the file is free.
- **The UI is connected** (ST-085). A tray icon and the recording pill show live state; the diagnostics
  window is filled by the service; discard and erase need a typed confirmation the service issued moments
  before. **Review and History are real panes** fed over the pipe: `get_session`, `get_frame`, the three
  frame edits and `save_draft` (#138); the shell shows the draft that just became ready and opens any
  session from History (#140). Settings is a sentence until ST-081. The UI tells the service every two seconds that the
  pill is on screen and where; the service treats a pill it has not heard from in six seconds as absent
  and suppresses capture until one is back (INV-4).
- The session state machine owns the lifecycle. Sources can only write while recording (INV-6). The
  input hooks are installed while a session exists and removed when it ends (#129).
- **A captured frame never touches the disk unredacted.** It waits in a bounded in-memory queue (depth
  4), the redaction worker takes it from there, and only the redacted, downscaled result is stored
  (ADR-0006). A frame the OCR engine cannot read, or that arrives when the queue is full, is dropped and
  counted, and the count reaches the draft so it hedges (ADR-0004). `NothingUnredactedIsEverInTheStore`
  reads the bytes back to prove it.
- Redaction finds SSNs, Luhn-valid cards, credential shapes, spoken passwords in the transcript and
  tenant patterns; overlapping regions are merged before masking.
- **Speech works.** The microphone opens with the session; Whisper `base.en` runs on two threads and
  is loaded when the first session wants it, not held all day from service start;
  the model download is hash-checked and cannot fill the disk; a voice-activity gate and a
  hallucination filter sit in front of the transcript; transcription runs off the microphone loop.
- **The drafting path is wired end to end.** Bundle → outbox → sender → `POST v1/sessions/summarize`
  with a device token → Gemini Flash → validator (invented frames, fake quotations, credentials,
  instruction-shaped text) → note in the store. One model call per session; the daily cap is checked
  before the call.
- Every HTTP call the service makes goes through the egress allowlist at the composition root. Anything
  that is not HTTPS, or not the model host or the configured backend host, is refused (INV-8).
- The audit log is hash-chained with a head anchor (#85), so truncation from either end is detected.
- Design tokens generate WPF dictionaries and web CSS from one source; the gallery renders in CI in
  dark, light and high contrast.

**Not built:** publishing (ST-077, ST-078), enrolment (ST-010 — a Development-only flag issues a device
token until then), settings and policy sync (ST-047, ST-081), a signed installer (ST-112), hosting
(ST-007), on-device drafting.

## 4. Decisions already made

- **.NET 10 LTS** (support for 8 ends November 2026).
- **OCR on the full-size frame**; only the display/upload copy is shrunk (ADR-0001 finding 2a).
- **Spec v0.4.1 (Q1–Q6):** HUD always visible during screen-share; Internal note type default; tray
  discard stops capture before confirming; Publish disabled after a failed draft; elevated-window
  wording; bulk discard confirms by count.
- **Spec v0.4.2:** `text.muted` and light `accent.primary` adjusted, `text.on-accent` added, and state
  colours are indicators, never body-size text.
- **Spec v0.4.3:** three S3 wording changes from ST-015/ST-074. Accepted 2026-09-15 (Part D, D-6).
- **A draft reports unrounded active minutes**; the client applies the tenant's billing rounding.
- **2026-09-15 sequencing** (Part D, D-1 to D-3): ST-060 waited for ST-042's engine, not its recall
  gate; the backend runs on Docker Postgres locally; the usability round gates the pilot, not the
  publish panel.
- **Model provider: Gemini Flash** (`gemini-3.6-flash`, low media resolution), chosen and measured
  2026-09-19. Anthropic and OpenAI stay swappable behind the same interface.
- **2026-09-22, four decisions from the efficiency review:** US only for now (no regional model
  endpoint); Whisper `base.en` on every machine, two threads; the microphone follows the session, not
  the recording state; Gemini billing is the owner's to enable and gates *measurement*, not M1.
- **Six ADRs, all Accepted:** 0001 (.NET stack), 0002 (platform-neutral Core), 0003 (per-user process,
  three-check pipe handshake), 0004 (a frame nobody read is not stored), 0005 (Windows adapters shared by
  both processes), 0006 (an unredacted frame waits in memory, accepted 2026-09-22).
- **The repository is public on purpose** — hosted Windows minutes are free for public repositories.
  Three repository settings, not workflow files, are what keep a stranger's pull request off the laptop.

## 5. Open items for the owner

- [ ] **Run M1.** `docs/dev/first-draft-end-to-end.md`, top to bottom, on the laptop. Everything runs
      there, including the backend, because the client refuses a backend that is not HTTPS and the only
      certificate the laptop trusts without ceremony is its own. The Review pane is wired as of #140, so
      the note can be read and judged.
- [x] ~~Choose the model provider and supply a key.~~ Gemini Flash. The key is in user secrets **on the
      Mac**; user secrets are per machine, so the laptop needs its own copy (the runbook says where).
- [ ] **Enable billing on the Gemini key.** The free tier (20 requests a day) is enough for M1 and not
      for the two measurements ST-063 still owes: the thinking-token budget and the repair rate.
- [ ] **Record the ten-minute narration** for ST-027's WER number. `research/fixtures/audio/README.md`
      says what it needs and the one command that scores it.
- [ ] **Have an MSP security lead read the privacy pack** (ST-114 AC1). The three documents were brought
      into line with the code on 2026-09-22; they are still unreviewed and say so.
- [ ] **ScreenConnect trial and a second Windows machine** for ST-030's golden sessions and ST-001 AC2.
- [ ] **Hosting decision** (Phase D). Docker Postgres on a laptop is fine until then.
- [ ] **ST-110 pilot-MSP baseline interviews**, and the market scan's §6 question in the same
      conversations.
- [ ] Two technician sessions on the Review wireframe (ST-014's remaining criterion).

## 6. Known gaps, deliberately left

- **ST-042 has no corpus recall gate**: it needs ST-030's staged captures.
- **OCR is a best-effort input to redaction, not a guarantee.** It will not read a long run of one
  ambiguous glyph. INV-1 cannot rest on the engine reading everything (ADR-0001 finding 9).
- **A 4K frame exceeds ST-025's budget and nothing fails**: about 309 ms against 120 ms, measured on the
  laptop. Windows.Graphics.Capture is the fix when it matters.
- **Each frame is decoded twice** — once to read it, once to paint on it. Passing the bitmap between
  them would hold about 33 MB a frame instead of 1.5 MB; a memory trade that was kept out of ADR-0006 on
  purpose and is its own decision when somebody wants it.
- **The full-disk case under-reports `frames_purged_unredacted`**: when the store refuses both the write
  and the record of the loss, the frame is let go rather than remembered, and the draft does not know to
  hedge. Named in the test; accepted as the price of not holding a frame for ever on a disk that will
  not empty.
- **The filmstrip decodes thumbnails at full size** (weaknesses P2-3): fine for a session of a few
  frames, 150 frames would cost hundreds of megabytes. ST-049's last open item, now that the pane exists.
- **The Review pane has not been seen on a real screen.** CI renders it over the fixture; the first live
  session is M1's.
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
| **Now** | **M1**: one real session, one real note, on the laptop. The runbook. | Owner |
| Right after | Write down what the first note got right and wrong; that is the first row of the eval corpus, before ST-030's ten sessions exist | Owner, ten minutes |
| Then, no owner input needed | **ST-009** (credential vault), **ST-078** (publish panel), **ST-093/094** shape against a fake PSA | Agent |
| Phase C proper | **ST-091/092** ConnectWise client and ticket search, **ST-077** ticket inference, **ST-095–097** Hudu | Agent, once there is a ConnectWise sandbox or API member and a Hudu key |
| In parallel, when the inputs exist | ST-027's WER number (the recording), ST-030 and ST-062 (ScreenConnect + second machine), ST-063's two measurements (billing) | Owner supplies; agent runs |
| Phase D | Hosting, enrolment, settings, installer, the security-lead review, the pilot | Both |

**Why this order.** The product has never drafted a note from a real session. M1 is the one thing that
tells us whether the next month goes into publishing or into fixing what the first note got wrong, and
it is a half-hour of the owner's time. There is a screen to read the note on now. Phase C starts with the tickets that need no account so the
publish path exists by the time the ConnectWise and Hudu credentials do.

### Needs a decision or an account

ST-091/092 needs a ConnectWise API member (a sandbox works until the pilot). ST-095 needs a Hudu key.
ST-007 needs a cloud account and a hosting decision. ST-017, ST-110, ST-111 and ST-114's review need
technicians, a pilot MSP, a lawyer and a security lead.

## 8. Handy commands

| Want to… | Do |
|---|---|
| Run client tests | `dotnet test client/ScreenTail.sln` |
| Run backend tests | `dotnet test backend/ScreenTail.Backend.sln` (SQLite; never calls a model) |
| Run research tests | `cd research && pytest` |
| Run the product on the laptop | `powershell -ExecutionPolicy Bypass -File .\scripts\windows\run-local.ps1` |
| Get the first real note | `docs/dev/first-draft-end-to-end.md` |
| See which secrets the backend has, without retyping them | `dotnet user-secrets list --project backend/src/ScreenTail.Api` (prints values — mind the shoulder) |
| Render the component gallery | on Windows: `dotnet run --project client/ScreenTail.UI -- --gallery` |
| Render the note pane or the HUD | on Windows: `dotnet run --project client/ScreenTail.UI -- --note --screenshot <dir>` / `-- --hud --screenshot <dir>` |
| Read the closed weakness review | `docs/review/weaknesses.md` — the status block at the top says what is left |
| See the market and the idea list | `docs/product/2026-09-15-market-scan-and-ideas.md` — §2.4 for the wedge, §4 for ideas |
| Look at a fixture session | `research/fixtures/handcrafted/<name>/session.json` + its `frames/` |
| Regenerate schema or token outputs | `npm run codegen` in `shared/schema` or `shared/design` |
| Open the wireframes | `open docs/ux/wireframes/index.html` |
| Trigger a laptop spike run by hand | `gh workflow run spike-windows.yml --ref main` |
| See what the laptop measured | `gh run download <run-id>` then read the text files; images are never uploaded |
| Check the audit log verifies | `store.VerifyAuditAsync()` — the export says so too |
