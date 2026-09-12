# ScreenTail — project standing

**Snapshot taken:** 2026-09-12, ~15:30 CDT
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next.

---

## 1. The one thing that needs you

**Thirteen pull requests are waiting, twelve of them in one chain.** Nothing has merged to `main` yet, so
every ticket since ST-002 is sitting on a branch built on the branch before it. That works, and CI is
green on all of it, but the stack only gets harder to review the longer it waits, and a change you want
in ST-002 now has nine descendants to rebase.

**My recommendation:** merge #3 today, even if you read nothing else. It's the repo skeleton and CI; the
rest rebases onto `main` from there. Then work down §2 at whatever pace suits you.

## 2. What exists

| Piece | Where | State |
|---|---|---|
| GitHub repo (private) | https://github.com/hiratinspace/ScreenTail | `main` holds only the plan docs; **11 open PRs** |
| Build plan | `Build Plan/` | Spec at v0.4.2 (ST-014 decisions + the contrast fixes); guide amended for .NET 10 and ADR-0002. All amendments live on branches |
| Windows test loop | GitHub-hosted Windows VM + spare laptop (runner `SCREENTRAIL`) | Working. Laptop **off**; every PR still runs on the hosted VM, which also runs the DPAPI tests and renders the UI gallery |
| Client test suite | `client/` | **178 tests**, green on macOS and on Windows CI |
| Research suite | `research/` | **62 tests** (fixtures + prompt contract), green |
| Branch protection for `main` | GitHub ruleset "main" | Created, **switched off** until #3 merges |

## 3. Pull requests, in merge order

All are drafts with their acceptance criteria ticked (or explicitly not ticked) in the description.
Merge with "Rebase and merge" or a merge commit, **not squash** — squash rewrites the commit messages the
conventional-commit check depends on.

| Order | PR | Ticket | Notes |
|---|---|---|---|
| 1 | [#3](https://github.com/hiratinspace/ScreenTail/pull/3) | ST-002 repo, CI, quality gates | Base of everything. **Needs your yes on ADR-0002** (`ScreenTail.Core`) |
| 2 | [#4](https://github.com/hiratinspace/ScreenTail/pull/4) | ST-003 session schema | Review found the C# side accepted documents the schema rejects → `SessionValidator` |
| 3 | [#5](https://github.com/hiratinspace/ScreenTail/pull/5) | ST-005 encrypted store | SQLCipher, two-stage frame lifecycle, INV-1 enforced in three places |
| 4 | [#6](https://github.com/hiratinspace/ScreenTail/pull/6) | ST-004 service + UI over an authenticated pipe | **Needs your yes on ADR-0003** (per-user process, not a Windows Service) |
| 5 | [#8](https://github.com/hiratinspace/ScreenTail/pull/8) | ST-020 session state machine | Redaction grace, INV-6 gating, crash recovery |
| 6 | [#9](https://github.com/hiratinspace/ScreenTail/pull/9) | ST-044 retention purge + delete-everything | Includes a retention-bypass fix (§5) |
| 7 | [#10](https://github.com/hiratinspace/ScreenTail/pull/10) | ST-066 suggested time entry | Small; pure arithmetic over the machine's active time |
| 8 | [#11](https://github.com/hiratinspace/ScreenTail/pull/11) | ST-042 redaction engine | **Partial by design** — the corpus recall gate needs ST-030 |
| 9 | [#12](https://github.com/hiratinspace/ScreenTail/pull/12) | ST-006 fixture bundles + redaction seed | Frames are **drawn, not staged** — read the deviation note in the PR |
| 10 | [#13](https://github.com/hiratinspace/ScreenTail/pull/13) | ST-061 note prompt + output contract | The prompt's rules as enforced post-conditions |
| 11 | [#2](https://github.com/hiratinspace/ScreenTail/pull/2) | ST-014 wireframes | Still needs the two technician sessions, but the spec decisions are in it |
| 12 | [#7](https://github.com/hiratinspace/ScreenTail/pull/7) | ST-016 tokens, components, gallery | **Needs your veto or nod on the v0.4.2 colours** |
| 13 | [#1](https://github.com/hiratinspace/ScreenTail/pull/1) | ST-001 spike + ADR-0001 | Independent of everything. AC2 (RDP opacity) needs a second machine |

**They merge in this order with no conflicts** — I dry-ran the whole sequence into a scratch copy of
`main` after each rebase, which is how I found and removed the two that were there (a CI workflow clash
between the schema and design jobs, and both branches appending to the spec's amendments table).
#2 and #7 used to branch off `main` and `#3`; I rebased them onto the tip so the chain is linear.
If you merge out of order, expect those two conflicts back.

Chain: #3 ← #4 ← #5 ← #6 ← #8 ← #9 ← #10 ← #11 ← #12 ← #13 ← #2 ← #7. #1 stands alone.

## 4. Decisions already made
- **.NET 10 LTS** (support for 8 ends November 2026).
- **OCR on the full-size frame**; only the display/upload copy is shrunk (ADR-0001 finding 2a).
- **Spec v0.4.1 (Q1–Q6):** HUD always visible during screen-share; Internal note type default; tray discard stops capture before confirming; Publish disabled after a failed draft; elevated-window wording; bulk discard confirms by count.
- **Spec v0.4.2 (pending your veto):** the CI contrast test found 10 text pairs under 4.5:1 → `text.muted` and light `accent.primary` adjusted, `text.on-accent` added, and a rule that state colours are indicators, never body-size text.
- **A draft reports unrounded active minutes**; the client applies the tenant's billing rounding. Rounding in both places would round twice (ST-061 + ST-066).
- **ADR-0002 (proposed):** platform-neutral `ScreenTail.Core`. Working as intended — every ticket since has been finished on the Mac.
- **ADR-0003 (proposed):** capture service is a per-user background process; pipe handshake = user ACL + verified client executable + per-run token.

## 5. What this stretch found
Things that were wrong and are now fixed, each with a test that would catch it again:
- **A retention bypass** (flagged by the automated security review): raw data from a session that never reached finalize — what a crash leaves behind — would have sat on disk for ever, and sessions finalized before the migration would have been exempt too. Retention now ages a session from its end *or* its creation, and only the session being recorded right now is exempt. One of my own tests had asserted the hole as intended behaviour.
- **The spec's palette failed the spec's own contrast rule** in 10 places → v0.4.2.
- **WPF distorts corner radii larger than half an element's height** — found in the CI gallery render, not by eye.
- **Crash recovery restarted a recovered session's clock at zero**, so the timeline came back unordered.
- **My ST-006 fixtures billed twice** — 15/30/45-minute drafts on 6–12 minute sessions — caught by ST-061's own checks.
- **A flaky test of my own making**: fixed `ts_ms` values racing the machine's real clock. It only showed once the suite got slow enough.
- **The generated C# accepted sessions the JSON Schema rejects** (earlier in the week) → `SessionValidator`.
- **A redaction detector that ran out of time threw instead of reporting** — a busy CI agent tripped the
  100 ms pattern budget mid-run. Worse than the crash: a scan that doesn't finish isn't a clean scan, so
  the engine now says so and the caller purges the frame rather than storing one nobody checked.

## 6. Decisions the owner needs to make
- [ ] Accept ADR-0001 (stack), ADR-0002 (Core project), ADR-0003 (hosting + pipe). Recommendation: yes to all three.
- [ ] Veto or accept spec v0.4.2 (PR #7).
- [ ] Start merging (§1).
- [ ] ST-006's frames are **drawn, not captured**. Fine as a stand-in, but say if you'd rather wait for staged captures.

## 7. Next steps
### Build agent (no owner, no laptop)
The well of work that needs neither is nearly dry — most remaining tickets need either Windows APIs, a
cloud account, or your decisions. What's left: ST-062 (draft-quality eval harness, now that fixtures and
the prompt exist), ST-043's matching logic, and backend tickets once you decide on hosting.

### Needs the laptop (turn it on and tell me)
ST-021 capability checks · ST-022 foreground detection · ST-024 hooks · ST-025 screenshots ·
ST-027 speech. Most of their logic gets written here first; their acceptance tests run on the laptop
through CI. Also ST-004's "starts at login" and a by-eye check of ST-016's focus rings.

### Only you
- **ST-110 pilot-MSP baseline measurement** — Sprint 1 background work; it gates the pilot metric, so the sooner it starts the better.
- Two technician sessions on the Review wireframe (ST-014).
- RDP check for ST-001 (5 minutes with a second Windows machine).
- Laptop: automatic sign-in and lock-screen settings so unattended runs survive reboots.

## 8. Handy commands

| Want to… | Do |
|---|---|
| See open PRs | `gh pr list` |
| Run client tests here (Mac) | `dotnet test client/ScreenTail.sln` |
| Run research tests | `cd research && pytest` |
| Render the component gallery | on Windows: `dotnet run --project client/ScreenTail.UI -- --gallery` |
| Look at a fixture session | `research/fixtures/handcrafted/<name>/session.json` + its `frames/` |
| Regenerate fixtures (macOS only) | `cd research && python tools/… render_fixtures.py` — see `fixtures/README.md` |
| Trigger a laptop spike run | `gh workflow run spike-windows.yml --ref <branch>` |
| Open the wireframes | `open docs/ux/wireframes/index.html` (ST-014 branch) |
