# ScreenTail — project standing

**Snapshot taken:** 2026-09-12, ~16:00 CDT
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next.

---

## 1. Where things stand

**Everything is merged.** All thirteen pull requests went into `main` in dependency order, every branch is
deleted, and `main` is green in CI. There is no review backlog: the next piece of work starts from a clean
trunk.

Eleven tickets are done or substantially done: ST-001 (spike), ST-002 (repo/CI), ST-003 (schema),
ST-004 (service + IPC), ST-005 (store), ST-006 (fixtures), ST-014 (wireframes), ST-016 (design system),
ST-020 (state machine), ST-042 (redaction engine, partial), ST-044 (retention), ST-061 (note prompt),
ST-066 (time entry).

## 2. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (private) | https://github.com/hiratinspace/ScreenTail | `main` only; no open PRs |
| Build plan | `Build Plan/` | Spec at **v0.4.2** — ST-014's six decisions and the contrast fixes are both in the amendments table |
| Client | `client/` | `.Shared` (schema + IPC), `.Core` (store, sessions, privacy, intel), `.Service`, `.UI`. **178 tests** |
| Backend / web | `backend/`, `web/` | Skeletons with CI gates; nothing built on them yet |
| Research | `research/` | Fixtures, note prompt, output contract. **62 tests** |
| Windows test loop | Hosted Windows VM + spare laptop (`SCREENTRAIL`) | Hosted runs on every PR. Laptop **off** |
| Branch protection | Ruleset "main" | **Created but still disabled** — see §5 |

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
- Retention removes raw data after the tenant's window and "delete everything" clears store, key,
  tokens and credentials (INV-12).
- The note prompt is versioned with a strict output schema and post-conditions that reject invented
  steps, fake quotations, dangling frame references and leaked secrets.
- Design tokens generate WPF dictionaries and web CSS from one source; the component gallery renders in
  CI on Windows in dark, light and high contrast.

**Nothing captures anything yet.** Hooks, screenshots, foreground detection and speech are the next
block of work, and all of it needs Windows.

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
- [ ] **`HW_RUNNER` is still `true` while the laptop is off.** Any PR touching `spike/` queues a job that
      can never start — it happened on ST-001 and I cancelled the run. Set it to `false` until the
      laptop is back, or just remember why a check hangs.
- [ ] **Read the three ADRs** and mark them Accepted (or push back).
- [ ] **ST-110 pilot-MSP baseline measurement** — Sprint 1 background work, and it gates the pilot's
      success metric, so the earlier it starts the better.
- [ ] Two technician sessions on the Review wireframe (ST-014's remaining criterion).
- [ ] RDP opacity check for ST-001 AC2 (5 minutes with a second Windows machine).

## 6. Known gaps, deliberately left
- **ST-042** has no corpus recall gate: it needs ST-030's staged captures. The engine and its unit tests
  are in; the ≥ 98% recall / ≤ 2% false-positive criterion is untested.
- **ST-006's frames are drawn, not captured.** Good enough for UI and pattern work, useless for judging
  OCR quality. `research/fixtures/README.md` says so where someone will see it.
- **ST-014** still needs the two technician sessions before its Q1/Q2 decisions are confirmed.
- **ST-016's focus rings** were confirmed by code and CI render, not by tabbing through the app.

## 7. Next steps
### Needs the laptop (turn it on and say so)
ST-021 capability checks · ST-022 foreground detection · ST-024 input hooks · ST-025 screenshots ·
ST-027 speech. Their logic gets written on the Mac first; their acceptance tests run on the laptop
through CI. Also: ST-004's "starts at login" and a by-eye pass over ST-016's focus rings.

### Needs a decision or an account
Backend tickets (ST-007 infrastructure onward) need a cloud account and a hosting decision. ST-063
(cloud drafting) needs a model provider.

### Can be done without either
ST-062 draft-quality evaluation harness — the fixtures and the prompt contract it needs now both exist.

## 8. Handy commands

| Want to… | Do |
|---|---|
| Run client tests | `dotnet test client/ScreenTail.sln` |
| Run research tests | `cd research && pytest` |
| Render the component gallery | on Windows: `dotnet run --project client/ScreenTail.UI -- --gallery` |
| Look at a fixture session | `research/fixtures/handcrafted/<name>/session.json` + its `frames/` |
| Regenerate fixtures (macOS only) | `cd research && python fixtures/tools/render_fixtures.py` |
| Regenerate schema or token outputs | `npm run codegen` in `shared/schema` or `shared/design` |
| Open the wireframes | `open docs/ux/wireframes/index.html` |
| Trigger a laptop spike run | `gh workflow run spike-windows.yml --ref main` |
