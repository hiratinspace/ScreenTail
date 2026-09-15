# ScreenTail — project standing

**Snapshot taken:** 2026-09-15, ~16:30 CDT
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next.

---

## 1. Where things stand

**`main` is green and has the whole capture path on it.** A click or a scene change becomes a frame, the
frame is staged encrypted and unreadable, the redaction worker reads it, masks what it finds and only then
shrinks it for storage. Hotkeys drive the session. Nothing is drafted yet.

> **Read `docs/review/weaknesses.md` before pointing this at a customer session.** A review on 2026-09-15
> found five ship-blockers, two of them invariant breaches: a frame the OCR engine reads nothing from is
> stored *as redacted* with nothing masked (INV-1), and there is no capture indicator at all in the
> running application (INV-4). Neither is a policy error — the decision classes are right — but the
> enforcement points are weaker than the policy objects throughout.

**Thirty-five tickets are done or substantially done:** ST-001 (spike), ST-002 (repo/CI), ST-003 (schema),
ST-004 (service + IPC), ST-005 (store), ST-006 (fixtures), ST-012 (threat model + client hardening),
ST-014 (wireframes), ST-015 (Review hi-fi), ST-016 (design system), ST-020 (state machine), ST-021
(capability checks), ST-022 (foreground detection), ST-023 (scope policy), ST-024 (input hooks), ST-025
(screenshot on click), ST-026 (scene sampling), ST-029 (hotkeys), ST-040 (password-field suppression),
ST-041 (OCR and redaction worker), ST-042 (redaction engine, partial), ST-043 (exclusions), ST-044
(retention), ST-045 (audit log), ST-046 (egress guard), ST-061 (note prompt), ST-066 (time entry),
ST-070 (shell), ST-071 (tray and diagnostics), ST-072 (recording HUD), ST-074 (note editor), ST-075
(screenshot strip), ST-079 (session history), ST-114 (privacy pack, partial), ST-027 (speech, partial).

**In flight:** nothing. No open pull requests.

**Two tickets are partial and say so:** ST-027 has its gating, model download and transcript assembly but
no audio capture and no WER number; ST-114 has its three documents but no security-lead review.

## 2. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (private) | https://github.com/hiratinspace/ScreenTail | `main` plus whatever is in flight |
| Build plan | `Build Plan/` | Spec at **v0.4.3** — ST-014's six decisions, the contrast fixes and ST-074's three S3 wording changes are all in the amendments table |
| Client | `client/` | `.Shared` (schema + IPC), `.Core` (store, sessions, privacy, capture, input, audit), `.Service`, `.UI`. **721 tests** on macOS, plus a Windows-only suite |
| Backend / web | `backend/`, `web/` | Skeletons with CI gates; nothing built on them yet |
| Research | `research/` | Fixtures, note prompt, output contract. **100 tests** |
| Windows test loop | Hosted Windows VM + spare laptop (`SCREENTRAIL`) | Both run on every PR touching capture. The laptop produces the numbers; the hosted runner's don't count |
| Branch protection | Ruleset "main" | **On.** PR required, `ci-ok` must be green, no force-push, no deletion |

## 3. What the code does today

- Capture service starts per user, serves an authenticated named pipe (user ACL + verified client
  executable + per-run token), recovers sessions a crash left behind, and runs retention hourly.
- The session state machine owns the lifecycle. Capture sources can only write while recording, so pause
  and *user* suppression are enforced by construction (INV-6). **The scope half is not:** the excluded-app
  and out-of-scope drops live in `ClickCaptureLoop`, which no test covers — delete them and the suite
  stays green. See weaknesses P0-4.
- The store is SQLCipher-encrypted. A frame enters redaction-pending and nothing but the redaction worker
  can read it back; INV-1's **read** path is thorough — every read filters the flag. **The write path is
  not:** a frame the OCR engine returns nothing for skips the discard and is stored with the flag cleared
  and nothing masked. See weaknesses P0-1.
- The redaction engine finds SSNs, Luhn-valid cards, credential shapes, password cues and tenant
  patterns, and maps matches back to OCR word boxes. A scan that can't finish says so, so the caller
  purges the frame instead of storing one nobody checked.
- Screenshots are staged at the size they were captured, never downscaled first: the worker has to read
  the text before it masks it, and 1600 px was measured illegible below 200% scaling (ADR-0001 finding 2a).
- The screen is sampled once a second and a frame kept only when it has actually changed, so the error
  dialog a technician reads without clicking is still captured, and the desktop they stare at for a minute
  is not photographed sixty times.
- Capture stops while a password field has focus (UI Automation) and for ten seconds after the OCR text
  looks like a sign-in screen. Both write an interval to the audit log.
- The audit log is hash-chained: editing, deleting or reordering a row breaks it from there on. Its export
  carries counts, states and hosts, never content.
- Retention removes raw data after the tenant's window (INV-12). **"Delete everything" is implemented and
  unreachable** — there is no Settings screen and no IPC command that calls it.
- The note prompt is versioned with a strict output schema and post-conditions that reject invented
  steps, fake quotations, dangling frame references and leaked secrets.
- Design tokens generate WPF dictionaries and web CSS from one source; the component gallery renders in
  CI on Windows in dark, light and high contrast.

**Capture works; speech, drafting and the UI do not.** A session started by a remote tool taking focus
records clicks and typing counts, photographs clicks and scene changes, reads every frame with the OS OCR
engine, masks what the pattern library finds, and stops capturing when it sees a password field or a
sign-in screen. What it cannot do yet: hear anything (ST-027), write a note (ST-060/063), or publish
(ST-077/078).

**The UI process is not an application yet.** It has no project reference to the service, never opens the
pipe, and every window it renders is fed hard-coded sample data. Review, the HUD, history, the tray and
the diagnostics panel are all built, tested and rendered in CI — and reachable only through the
`--gallery` / `--note` / `--hud` screenshot harnesses. Wiring the UI to the service is the single
highest-leverage piece of work left, and it is not currently a ticket.

## 4. Decisions already made
- **.NET 10 LTS** (support for 8 ends November 2026).
- **OCR on the full-size frame**; only the display/upload copy is shrunk (ADR-0001 finding 2a).
- **Spec v0.4.1 (Q1–Q6):** HUD always visible during screen-share; Internal note type default; tray discard stops capture before confirming; Publish disabled after a failed draft; elevated-window wording; bulk discard confirms by count.
- **Spec v0.4.2:** `text.muted` and light `accent.primary` adjusted, `text.on-accent` added, and state colours are indicators, never body-size text — the CI contrast test found 10 pairs under 4.5:1.
- **A draft reports unrounded active minutes**; the client applies the tenant's billing rounding. Rounding in both places would round twice.
- **Spec v0.4.3:** three S3 wording changes from ST-015/ST-074 — the disabled-Publish reason moves out of
  a tooltip, the offline no-draft message stops telling you to retry something that cannot succeed, and
  the save indicator gains "Not saved — retrying". **Pending your veto, along with v0.4.2.**
- **ADR-0001** (.NET stack), **ADR-0002** (platform-neutral `ScreenTail.Core`), **ADR-0003** (per-user
  process, not a Windows Service; three-check pipe handshake). All three say **"Proposed"**, and all three
  say they become Accepted when ST-001 / ST-002 / ST-004 merge — which they all have. This is clerical,
  not an open decision. ADR-0001's title also still says .NET 8 against a .NET 10 project.

## 5. Open items for the owner
- [x] ~~Turn on the `main` ruleset.~~ **Done 2026-09-12.** `main` now requires a pull request and a
      green `ci-ok`, and refuses force-pushes and deletion. Nobody is on the bypass list, so this
      applies to you too: work on a branch, open a PR, let CI finish. Squash merging is still
      permitted by the ruleset — avoid it, since it discards the per-commit `Refs: ST-###` footers.
- [ ] **Decide on the P0 findings** in `docs/review/weaknesses.md`. Two are invariant breaches and three
      of the five are under twenty lines of code between them. This gates whether the product can be
      pointed at a customer at all.
- [ ] **Veto or accept spec amendments v0.4.2 and v0.4.3.** Both are marked pending in §9 of the spec.
- [ ] **Record the ten-minute narration** for ST-027's WER criterion. `research/fixtures/audio/README.md`
      says exactly what it needs and why a synthesised recording would measure the wrong thing. This
      unblocks ST-028, ST-030, ST-080 and ST-123.
- [ ] **Have an MSP security lead read the privacy pack** (ST-114 AC1). Until then answer 7.4 says it is
      unreviewed, and it must not be represented otherwise.
- [ ] **`SCREENTRAIL` is switched off and `HW_RUNNER` is `false`.** When it comes back: sign in to the
      desktop, then set `HW_RUNNER=true`. Note the runner must run **interactively** (`run.cmd` in a
      console on the unlocked desktop), not as a Windows service — a service has no desktop and every
      capture, hook and password-field check skips. The foreground lock is now lifted by the test harness
      itself, so the console holding the foreground is no longer a problem.
- [ ] Flip the three ADRs to Accepted (clerical — see §4).
- [ ] **ST-110 pilot-MSP baseline measurement** — Sprint 1 background work, and it gates the pilot's
      success metric, so the earlier it starts the better.
- [ ] **Decide which of the sixteen ideas in `docs/product/2026-09-15-market-scan-and-ideas.md`
      become tickets.** The scan's one open question — faster notes, compounding knowledge, or
      proof for disputes — belongs in the ST-110 interviews.
- [ ] Two technician sessions on the Review wireframe (ST-014's remaining criterion).
- [ ] RDP opacity check for ST-001 AC2 (5 minutes with a second Windows machine).

## 6. Known gaps, deliberately left
- **ST-042** has no corpus recall gate: it needs ST-030's staged captures. The engine and its unit tests
  are in; the ≥ 98% recall / ≤ 2% false-positive criterion is untested.
- **OCR is a best-effort input to redaction, not a guarantee.** Windows.Media.Ocr reads a 1080p dialog in
  31–64 ms and got 18 of 18 labels, but it will not read a long run of one ambiguous glyph — the canonical
  `4111 1111 1111 1111` test card comes back as nothing at all. Real card numbers are not sixteen repeated
  ones, so it does not change the design, but INV-1 cannot rest on the engine reading everything. See
  ADR-0001 finding 9.
- **A 4K frame exceeds ST-025's budget and nothing fails.** Measured on the laptop: 13.9 ms fixed +
  35.6 ms/MP, so a 4K frame is about **309 ms against a 120 ms budget**. The test asserts a 1.37 MP window
  (62 ms, passes) and only *records* the 4K extrapolation. Windows.Graphics.Capture is the fix when it
  matters. See weaknesses P2-2.
- **Redaction's tail is three times its budget.** 200 frames at 1920×1080: median 185 ms, p95 243 ms —
  comfortably inside 700 ms — but worst case **2012 ms**. A large part of that is the pattern library
  running twice per frame (weaknesses P2-1, ~10 lines to fix).
- **The foreground watcher's CPU number is measured on an idle desktop**, which is not the condition
  ST-031 budgets for. It also subscribes to roughly twenty WinEvent types rather than two (P0-5).
- **ST-006's frames are drawn, not captured.** Good enough for UI and pattern work, useless for judging
  OCR quality. `research/fixtures/README.md` says so where someone will see it.
- **ST-014** still needs the two technician sessions before its Q1/Q2 decisions are confirmed.
- **ST-016's focus rings** were confirmed by code and CI render, not by tabbing through the app.

## 7. Next steps

### Nothing is blocked on the laptop right now
ST-040 and ST-045 both merged with their hardware tests actually running — 46 tests, 0 skipped. The
foreground problem that made them skip for three days was ours, not the machine's: Windows refuses
`SetForegroundWindow` to a process that has received no input, and the harness now lifts that lock itself.

### The work that matters most, in order
1. **The three cheap P0s** — weaknesses P0-1 (one line), P0-3 (one clause), P0-5 (~5 lines). Two invariant
   breaches and a correctness bug feeding scope decisions the wrong window.
2. **P1-4, the CI skip gate.** 63% of the hardware suite can vanish green, and `hardware-checks` is not a
   required check. Until this exists no hardware evidence means anything — including evidence for the
   fixes above.
3. **P2-1**, the doubled pattern-library pass: ~10 lines off a budget already being exceeded.
4. **P0-4**, the scope-enforcement test, with the small refactor it needs to be testable at all.
5. **Wire the UI to the capture service.** Large, not currently a ticket, and it closes P0-2, P1-1, P1-2,
   P1-6 and three ⚠ answers in the questionnaire at once.

### Then
**ST-076** (timeline panel) needs ST-028, which needs ST-027's audio. **ST-078** (publish) needs ST-017
and ST-092. **ST-030** unblocks ST-042's recall gate.

### Needs a decision or an account
Backend tickets (ST-007 onward) need a cloud account and a hosting decision. ST-063 (cloud drafting) needs
a model provider. ST-017 and ST-110 need technicians and a pilot MSP.

## 8. Handy commands

| Want to… | Do |
|---|---|
| Run client tests | `dotnet test client/ScreenTail.sln` |
| Run research tests | `cd research && pytest` |
| Render the component gallery | on Windows: `dotnet run --project client/ScreenTail.UI -- --gallery` |
| Render the note pane | on Windows: `dotnet run --project client/ScreenTail.UI -- --note --screenshot <dir>` |
| Render the HUD variants | on Windows: `dotnet run --project client/ScreenTail.UI -- --hud --screenshot <dir>` |
| Read the ranked weaknesses | `docs/review/weaknesses.md` — P0 section first |
| See the market and the idea list | `docs/product/2026-09-15-market-scan-and-ideas.md` — §2.4 for the wedge, §4 for ideas |
| Look at a fixture session | `research/fixtures/handcrafted/<name>/session.json` + its `frames/` |
| Regenerate fixtures (macOS only) | `cd research && python fixtures/tools/render_fixtures.py` |
| Regenerate schema or token outputs | `npm run codegen` in `shared/schema` or `shared/design` |
| Open the wireframes | `open docs/ux/wireframes/index.html` |
| Trigger a laptop spike run | `gh workflow run spike-windows.yml --ref main` |
| See what the laptop measured | `gh run download <run-id>` then read `measurements.txt` |
| Check the audit log verifies | `store.VerifyAuditAsync()` — the export says so too |
