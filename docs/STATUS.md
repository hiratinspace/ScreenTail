# ScreenTail — project standing

**Snapshot taken:** 2026-09-12, ~09:30 CDT (end of the second build session; the build agent worked on its own overnight per "keep working on things you can do without me")
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next.

---

## 1. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (private) | https://github.com/hiratinspace/ScreenTail | `main` still holds only the plan docs; **8 draft PRs** wait for you (see §2) |
| Build plan | `Build Plan/` | Spec amended to v0.4.1 (ST-014 decisions) and **v0.4.2 (contrast fixes, ST-016)**; guide amended for .NET 10 and the Core project (ADR-0002). All amendments live on branches |
| Windows test loop | GitHub-hosted Windows VM + spare laptop (runner `SCREENTRAIL`) | Working. Laptop is **off** right now; every PR still runs on the hosted VM, which also runs DPAPI tests and renders the UI gallery |
| Branch protection for `main` | GitHub ruleset "main" | Created, **switched off** until PR #3 merges |

## 2. Pull requests, in merge order

Each is a draft with its acceptance criteria ticked in the description. Merge with "Rebase and merge" or a merge commit, **not squash**. After #3 merges I turn on branch protection and retarget the stacked PRs.

| Order | PR | Ticket | CI | Notes |
|---|---|---|---|---|
| 1 | [#3](https://github.com/hiratinspace/ScreenTail/pull/3) | ST-002 repo, CI, quality gates | green | Base of everything. Includes one review fix (Vitest cleanup). **Needs your yes on ADR-0002** (`ScreenTail.Core`) |
| 2 | [#4](https://github.com/hiratinspace/ScreenTail/pull/4) | ST-003 session schema | green | Review found the C# side accepted documents the schema rejects → `SessionValidator` added; 34 tests |
| 3 | [#5](https://github.com/hiratinspace/ScreenTail/pull/5) | ST-005 encrypted store | green (50/50 on Windows incl. DPAPI) | SQLCipher, two-stage frame lifecycle, INV-1 at three layers |
| 4 | [#6](https://github.com/hiratinspace/ScreenTail/pull/6) | ST-004 service + UI over authenticated pipe | green (76/76 on Windows) | **Needs your yes on ADR-0003** (per-user background process, not a Windows Service; the three-check handshake) |
| 5 | [#8](https://github.com/hiratinspace/ScreenTail/pull/8) | ST-020 session state machine | running at snapshot time | Grace, INV-6 gating, crash recovery; 13 tests against the real store |
| 6 | [#7](https://github.com/hiratinspace/ScreenTail/pull/7) | ST-016 tokens + WPF components + gallery | green; re-run after a pill-radius fix in progress | Independent of the stack above (based on #3). **Needs your veto or nod on the v0.4.2 token changes** |
| 7 | [#2](https://github.com/hiratinspace/ScreenTail/pull/2) | ST-014 wireframes | n/a (docs) | Still needs the two technician sessions (script in `docs/ux/wireframes/README.md`) |
| 8 | [#1](https://github.com/hiratinspace/ScreenTail/pull/1) | ST-001 spike + ADR-0001 | hosted green; laptop run green | AC2 (RDP opacity) still needs a second machine. **Needs your accept on ADR-0001** |

Stack: #3 ← #4 ← #5 ← #6 ← #8, and #3 ← #7. #2 and #1 are independent of all.

## 3. Decisions already made
- **.NET 10 LTS** (support for 8 ends November 2026).
- **OCR runs on the full-size screenshot on the device**; only the display/upload copy is shrunk (ADR-0001 finding 2a).
- **Spec v0.4.1 (Q1–Q6):** HUD always visible during screen-share; Internal note type default; tray discard stops capture before confirming; Publish disabled after a failed draft; elevated-window wording; bulk discard confirms by count.
- **Spec v0.4.2 (ST-016, pending your veto):** the CI contrast test found 10 pairs under 4.5:1 → `text.muted` and light `accent.primary` adjusted, `text.on-accent` added (dark text on the dark theme's bright blue), and a rule: state colours are indicators, never body-size text.
- **ADR-0002 (proposed):** platform-neutral `ScreenTail.Core` so client logic is test-driven on the Mac. Working exactly as intended: every ticket since has been finished here.
- **ADR-0003 (proposed):** capture service is a per-user background process in the signed-in session; UI is a view over it; pipe handshake = user ACL + verified client executable + per-run token.

## 4. What the overnight stretch found
- **Review pass** (hand-done; the multi-agent reviewer hit the session limit): Vitest wasn't cleaning up between tests; the generated C# accepted sessions the schema rejects (pending frames with OCR text, dangling references, unordered timestamps, any `schema_version`). Both fixed with tests.
- **The spec's own palette failed its own contrast rule** in 10 places (§3, v0.4.2).
- **WPF distorts corner radii larger than half the height**; the CI gallery render showed it, fixed.
- **Crash recovery had an ordering bug** (recovered timeline restarted at 0); the schema validator caught it in the state-machine tests.
- **Self-hosted runner constraints** are documented in `docs/dev/windows-test-loop.md`: no admin (so no `setup-dotnet`), no PowerShell 7.

## 5. Decisions the owner needs to make
- [ ] Accept ADR-0001 (stack), ADR-0002 (Core project), ADR-0003 (hosting + pipe). Recommendation: yes to all three.
- [ ] Veto or accept the spec v0.4.2 token changes (PR #7).
- [ ] Merge in the order in §2.
- [ ] Keep going with more tickets before the first merge? The stack is five deep; review gets harder the longer it waits.

## 6. Next steps
### Build agent (no owner or laptop needed)
- **ST-044** retention purge (INV-12) — small, store-only. *In progress at snapshot time.*
- **ST-066** suggested time-entry calculation — pure logic, uses the machine's active time.
- **ST-042** pattern-based redaction engine (regex + Luhn + transcript scrub) — the engine and unit tests; the recall gate waits for the real corpus (ST-030).
- **ST-061** note-generation prompt and output schema — research; the fixture-driven check waits for ST-006.

### Needs the laptop (turn it on and tell me)
- ST-021 capability checks, ST-022 foreground detection, ST-024 hooks, ST-025 screenshots, ST-027 speech: Windows adapters. Most of their logic will be written here first; their acceptance tests run on the laptop through CI.
- ST-004's "starts at login" and ST-016's focus-ring check by eye.

### Only you
- **ST-110 pilot-MSP baseline measurement** (Sprint 1 background work; gates the pilot metric).
- Two technician sessions on the Review wireframe (ST-014).
- RDP check for ST-001 (5 min with a second Windows machine).
- Laptop: automatic sign-in and lock-screen settings so unattended runs survive reboots.

## 7. Handy commands

| Want to… | Do |
|---|---|
| See open PRs | `gh pr list` |
| Trigger a laptop spike run | `gh workflow run spike-windows.yml --ref <branch>` |
| Check the laptop runner | `gh api repos/hiratinspace/ScreenTail/actions/runners` |
| Run client tests here (Mac) | `dotnet test client/ScreenTail.sln` (on any branch from #3 up) |
| Render the component gallery | on Windows: `dotnet run --project client/ScreenTail.UI -- --gallery` (`--screenshot <dir>` to save PNGs) |
| Open the wireframes | `open docs/ux/wireframes/index.html` (ST-014 branch) |
| Regenerate schema or token outputs | `npm run codegen` in `shared/schema` or `shared/design` |
