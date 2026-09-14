# ScreenTail — project standing

**Snapshot taken:** 2026-09-13, ~16:40 CDT
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next.

---

## 1. Where things stand

**`main` is green and has the whole capture path on it.** A click or a scene change becomes a frame, the
frame is staged encrypted and unreadable, the redaction worker reads it, masks what it finds and only then
shrinks it for storage. Hotkeys drive the session. Nothing is drafted yet.

Twenty-six tickets are done or substantially done: ST-001 (spike), ST-002 (repo/CI), ST-003 (schema),
ST-004 (service + IPC), ST-005 (store), ST-006 (fixtures), ST-014 (wireframes), ST-015 (Review hi-fi),
ST-016 (design system), ST-020 (state machine), ST-021 (capability checks), ST-022 (foreground detection),
ST-023 (scope policy), ST-024 (input hooks), ST-025 (screenshot on click), ST-026 (scene sampling),
ST-029 (hotkeys), ST-041 (OCR and redaction worker), ST-042 (redaction engine, partial), ST-043
(exclusions), ST-044 (retention), ST-046 (egress guard), ST-061 (note prompt), ST-066 (time entry),
ST-070 (shell), ST-071 (tray and diagnostics), ST-074 (note editor).

**In flight:** ST-040 (password-field suppression) is complete and waiting on the laptop; ST-045 (audit
log) is built and stacked behind it.

## 2. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (private) | https://github.com/hiratinspace/ScreenTail | `main` plus whatever is in flight |
| Build plan | `Build Plan/` | Spec at **v0.4.3** — ST-014's six decisions, the contrast fixes and ST-074's three S3 wording changes are all in the amendments table |
| Client | `client/` | `.Shared` (schema + IPC), `.Core` (store, sessions, privacy, capture, input, audit), `.Service`, `.UI`. **586 tests** on macOS, plus a Windows-only suite |
| Backend / web | `backend/`, `web/` | Skeletons with CI gates; nothing built on them yet |
| Research | `research/` | Fixtures, note prompt, output contract. **100 tests** |
| Windows test loop | Hosted Windows VM + spare laptop (`SCREENTRAIL`) | Both run on every PR touching capture. The laptop produces the numbers; the hosted runner's don't count |
| Branch protection | Ruleset "main" | **On.** PR required, `ci-ok` must be green, no force-push, no deletion |

## 3. What the code does today

- Capture service starts per user, serves an authenticated named pipe (user ACL + verified client
  executable + per-run token), recovers sessions a crash left behind, and runs retention hourly.
- The session state machine owns the lifecycle. Capture sources can only write while recording, so
  pause and suppression are enforced by construction rather than by discipline (INV-6).
- The store is SQLCipher-encrypted. A frame enters redaction-pending and nothing but the redaction
  worker can read it back; INV-1 is enforced in the read API, in reference scrubbing, and in SQL CHECKs.
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
- Retention removes raw data after the tenant's window and "delete everything" clears store, key,
  tokens and credentials (INV-12).
- The note prompt is versioned with a strict output schema and post-conditions that reject invented
  steps, fake quotations, dangling frame references and leaked secrets.
- Design tokens generate WPF dictionaries and web CSS from one source; the component gallery renders in
  CI on Windows in dark, light and high contrast.

**Capture works; speech and drafting do not.** A session started by a remote tool taking focus records
clicks and typing counts, photographs clicks and scene changes, reads every frame with the OS OCR engine,
masks what the pattern library finds, and stops capturing when it sees a password field or a sign-in
screen. What it cannot do yet: hear anything (ST-027) or write a note (ST-060/063).

## 4. Decisions already made
- **.NET 10 LTS** (support for 8 ends November 2026).
- **OCR on the full-size frame**; only the display/upload copy is shrunk (ADR-0001 finding 2a).
- **Spec v0.4.1 (Q1–Q6):** HUD always visible during screen-share; Internal note type default; tray discard stops capture before confirming; Publish disabled after a failed draft; elevated-window wording; bulk discard confirms by count.
- **Spec v0.4.2:** `text.muted` and light `accent.primary` adjusted, `text.on-accent` added, and state colours are indicators, never body-size text — the CI contrast test found 10 pairs under 4.5:1.
- **A draft reports unrounded active minutes**; the client applies the tenant's billing rounding. Rounding in both places would round twice.
- **ADR-0001** (.NET stack), **ADR-0002** (platform-neutral `ScreenTail.Core`), **ADR-0003** (per-user
  process, not a Windows Service; three-check pipe handshake). All three are merged but still say
  **"Proposed"** in their front matter — flip them to Accepted when you've read them.

## 5. Open items for the owner
- [x] ~~Turn on the `main` ruleset.~~ **Done 2026-09-12.** `main` now requires a pull request and a
      green `ci-ok`, and refuses force-pushes and deletion. Nobody is on the bypass list, so this
      applies to you too: work on a branch, open a PR, let CI finish. Squash merging is still
      permitted by the ruleset — avoid it, since it discards the per-commit `Refs: ST-###` footers.
- [ ] **Stop `SCREENTRAIL` locking its screen.** This is the live blocker. A locked screen is the secure
      desktop: the runner still builds and OCR still works, but no test window can take the foreground, so
      every capture, hook and password-field check skips. It used to skip *silently* and the job went
      green; that now fails loudly with `NO-DESKTOP` instead. Fix: unlock, then Settings → Accounts →
      Sign-in options → "If you've been away, when should Windows require you to sign in again?" =
      **Never**, Dynamic lock **off**, no screen saver. Steps 2–3 of `scripts/windows/setup-test-laptop.ps1`.
- [ ] **Read the three ADRs** and mark them Accepted (or push back).
- [ ] **ST-110 pilot-MSP baseline measurement** — Sprint 1 background work, and it gates the pilot's
      success metric, so the earlier it starts the better.
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
- **A 4K frame still exceeds ST-025's budget**: 131–180 ms against 120 ms, and the grab dominates.
  Windows.Graphics.Capture is the fix when it matters.
- **What scene sampling costs per second is unmeasured.** It is one StretchBlt into a 17×16 bitmap, but
  ST-031 budgets 15% of the machine for all of ScreenTail and that deserves a real number.
- **ST-006's frames are drawn, not captured.** Good enough for UI and pattern work, useless for judging
  OCR quality. `research/fixtures/README.md` says so where someone will see it.
- **ST-014** still needs the two technician sessions before its Q1/Q2 decisions are confirmed.
- **ST-016's focus rings** were confirmed by code and CI render, not by tabbing through the app.

## 7. Next steps
### Blocked only by the laptop, which is switched off
**ST-040** (password-field suppression) is complete — the one test that matters proves UI Automation, not
the `EM_GETPASSWORDCHAR` fallback, is what answers, which is the whole reason the fallback is safe to keep.
**ST-045** (audit log) is built and stacked behind it. Also waiting: ST-025's capture timings and ST-024's
hook measurements, which have been skipping since the screen locked. `HW_RUNNER` is set to `false` so the
rest of CI can go green; **set it back to `true` when the laptop is on**, or the hardware tests will keep
skipping silently.

### Next to build, in order
1. **ST-075 screenshot strip** and **ST-076 publish pane** — the other two panes of Review. ST-074 put the
   note pane and its rules in `ScreenTail.Core/Review`, and both remaining panes have the same shape: the
   decisions in Core with tests, a thin WPF view, and a `--note`-style render on the Windows runner so the
   XAML is checked without a machine to look at.
2. **ST-027 speech pipeline** — Urgent, and the biggest unblocker left (ST-028, ST-030, ST-080, ST-123).
   Two things do not exist yet and neither is code: audio fixtures under `research/fixtures/audio/`, and
   the WER script the ticket names at `research/eval/wer.py`. The VAD gating, chunking, segment assembly
   and resumable model download can all be written and tested on the Mac; *WER ≤ 15% on a 10-minute
   narration* needs a real recording and a microphone.
3. **ST-028 transcript-to-timeline alignment** — falls straight out of ST-027.
4. **ST-030 dev-mode recorder and golden dataset** — unblocks ST-042's recall gate and ST-013.

### Needs a decision or an account
Backend tickets (ST-007 onward) need a cloud account and a hosting decision. ST-063 (cloud drafting) needs
a model provider.

## 8. Handy commands

| Want to… | Do |
|---|---|
| Run client tests | `dotnet test client/ScreenTail.sln` |
| Run research tests | `cd research && pytest` |
| Render the component gallery | on Windows: `dotnet run --project client/ScreenTail.UI -- --gallery` |
| Render the note pane | on Windows: `dotnet run --project client/ScreenTail.UI -- --note --screenshot <dir>` |
| Look at a fixture session | `research/fixtures/handcrafted/<name>/session.json` + its `frames/` |
| Regenerate fixtures (macOS only) | `cd research && python fixtures/tools/render_fixtures.py` |
| Regenerate schema or token outputs | `npm run codegen` in `shared/schema` or `shared/design` |
| Open the wireframes | `open docs/ux/wireframes/index.html` |
| Trigger a laptop spike run | `gh workflow run spike-windows.yml --ref main` |
| See what the laptop measured | `gh run download <run-id>` then read `measurements.txt` |
| Check the audit log verifies | `store.VerifyAuditAsync()` — the export says so too |
