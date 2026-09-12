# ScreenTail — project standing

**Snapshot taken:** 2026-09-12, 00:30 CDT (end of the first build session)
**Purpose:** one page to come back to: what exists, what's decided, what's open, and what happens next. Update it at the end of each working session.

---

## 1. What exists

In about a day and a half, the project went from three planning documents to a working repository with automated checks on real Windows hardware.

| Piece | Where | State |
|---|---|---|
| GitHub repo (private) | https://github.com/hiratinspace/ScreenTail | `main` still holds only the plan docs; all work is on branches awaiting merge |
| Build plan | `Build Plan/` | Spec amended to v0.4.1 (six decisions); guide amended for .NET 10 and the Core project. Both amendments live on branches, not yet on `main` |
| Windows test loop | GitHub-hosted Windows VM + the spare laptop (runner `SCREENTRAIL`, machine ITS-SPARES09) | Working. Every PR runs on both; the build agent can trigger laptop runs itself. See `docs/dev/windows-test-loop.md` |
| Branch protection for `main` | GitHub ruleset "main" | Created but **switched off** until PR #3 merges (see Next steps, step 1) |
| Repo variable `HW_RUNNER` | GitHub → Settings → Variables | `true`: the laptop CI job is enabled |

## 2. Ticket by ticket

| Ticket | PR | Done | Still open |
|---|---|---|---|
| **ST-001** spike: does .NET hold up? | [#1](https://github.com/hiratinspace/ScreenTail/pull/1) | Latency passes on the laptop (worst hook callback 0.445 ms vs 5 ms budget, 5 min of input under full load); overlay hidden from capture (laptop + hosted VM, with control case); 4K legibility measured and resolved by decision; ADR-0001 with 10 findings; all evidence in `docs/adr/evidence/0001/` | RDP opacity check (AC2) needs a second machine. ADR-0001 is "Proposed" until the owner accepts it |
| **ST-002** repo, CI, quality gates | [#3](https://github.com/hiratinspace/ScreenTail/pull/3) | Client/backend/web/research skeletons on .NET 10; CI green everywhere incl. Windows client build and backend container; README | Turn on branch protection after merge. Owner sign-off on ADR-0002 (`ScreenTail.Core`) |
| **ST-003** shared session format | [#4](https://github.com/hiratinspace/ScreenTail/pull/4) | `shared/schema/session.v1.json`; generated C# and TypeScript; examples that enforce INV-1/INV-2/INV-9; CI keeps generated code in sync; 19 tests green | Nothing technical. Stacked on #3, so it merges after it (retarget to `main` first) |
| **ST-014** wireframes | [#2](https://github.com/hiratinspace/ScreenTail/pull/2) | All 8 screens + 5 flows, clickable (`docs/ux/wireframes/index.html`); the 6 spec questions answered and written into the spec | Two technician "accept this note" sessions. Script and observation sheet: `docs/ux/wireframes/README.md` |

## 3. Decisions already made

- **.NET 10 LTS** instead of .NET 8 (support for 8 ends November 2026). Guide §5 amended on the ST-002 branch.
- **OCR runs on the full-size screenshot on the device**; only the display/upload copy is shrunk to 1600 px (ADR-0001 finding 2, option a). Affects ST-025 and ST-041.
- **Spec v0.4.1 (Q1–Q6):** HUD always visible during screen-share · Internal is the default note type · tray "Discard session…" stops capture before confirming · Publish disabled after a failed draft · elevated-window wording · bulk discard confirms by typing the count.
- **ADR-0002 (proposed, needs owner's yes):** platform-neutral `ScreenTail.Core` project so most client logic is testable on the Mac without a Windows round-trip.

## 4. What the spike taught us

The stack question is settled: .NET adds no perceptible input lag, and the native-hook fallback isn't needed. What the runs surfaced is that **throughput, not latency, is the real risk**. Recorded as ADR-0001 findings 8–10.

| Finding | Measured (laptop: i7-10510U, 16 GB) | Budget | Lands on |
|---|---|---|---|
| Speech-to-text running continuously eats CPU and memory | 41–66% CPU, 529 MB | 15% CPU, 600 MB (ST-031) | ST-027: transcribe only when someone is speaking (voice activity detection) |
| OCR is slow | 1.19 s per 1600×900 frame; larger frames coming | 700 ms median (ST-041) | ST-041: OCR only the relevant region, more workers, or Windows.Media.Ocr |
| Screenshot capture + encode is slow | p95 178 ms at 1920×1080 | 120 ms for 4K (ST-025) | ST-025: Windows.Graphics.Capture + WIC encoder instead of the GDI/GDI+ path |

Lesson learned twice (the runner setup, and the failed CI steps): **anything that hooks input or captures the screen must run inside the signed-in user's session**, never as a background Windows service (session 0 has no desktop). This shapes ST-004 directly.

## 5. Decisions the owner needs to make

- [ ] **Accept ADR-0001?** Recommendation: yes. The RDP gap can't change the stack choice.
- [ ] **Approve ADR-0002 (`ScreenTail.Core`)?** Recommendation: yes; it's why client tests run in seconds locally.
- [ ] **Merge order:** #3 → #4 (retarget to `main` first) → #2 → #1. Use "Rebase and merge" or a merge commit, **not squash** (squash rewrites commit messages and breaks the conventional-commit check).
- [ ] **Start ST-004** as the next build ticket?

## 6. Next steps

### Right after the go-ahead (build agent, about an hour)
1. Mark #3 ready; owner merges it.
2. Rebase #1 and #2 onto `main` so they get a `ci-ok` run; retarget #4 to `main`.
3. Switch on the `main` ruleset (PRs required, `ci-ok` required, no force-push or deletion).
4. Merge the rest in order. Delete `spike/` in a follow-up once ADR-0001 is accepted.

### ST-004 — capture service + UI + authenticated local channel (build agent, 2–3 days)
- Two processes: a per-user capture process started at sign-in, and the WPF UI, talking over a named pipe with per-session tokens. The UI can crash and restart without losing a recording.
- Acceptance: UI killed → capture continues and the restarted UI reattaches within 2 s · an unauthorized process sending `start` is rejected and audit-logged · round trip < 10 ms · contract versioned and documented (`docs/ipc-contract.md`).
- First a short ADR on hosting: a logon task in the user's session, not a Windows Service (see §4).
- Reattach and rejection tests run on the laptop through CI; nothing needed from the owner unless something surprises us.

### Sprint 2 after that (per Backlog Part C)
ST-005 encrypted local store with the two-stage frame lifecycle · ST-006 hand-crafted fixture sessions · ST-016 design tokens and the WPF component library (needs the wireframes merged).

### Only the owner can do these (in parallel, none urgent)
- **ST-110, pilot-MSP baseline measurement.** Scheduled as Sprint 1 background work: two weeks measuring how long techs spend on notes today (≥ 3 techs, ≥ 40 tickets) plus 5 interviews. It gates the pilot's success metric, so the sooner it starts the better. Output: `docs/pilot/stage0-baseline.md`.
- Two technician sessions on the Review wireframe (15 min each) to close ST-014.
- The RDP check for ST-001 when a second Windows machine is available (5 min: `spike\run-spike.ps1`, or just open `mstsc` during a run).
- On the laptop: automatic sign-in and lock-screen settings (printed by `setup-test-laptop.ps1`) so unattended runs survive reboots. Without them, runs only work while someone is signed in.

## 7. Handy commands

| Want to… | Do |
|---|---|
| See open PRs | `gh pr list` |
| Trigger a laptop spike run | `gh workflow run spike-windows.yml --ref <branch>` |
| Check the laptop runner is online | `gh api repos/hiratinspace/ScreenTail/actions/runners` |
| Run client tests here (Mac) | `dotnet test client/ScreenTail.sln` (on the ST-002/ST-003 branches) |
| Open the wireframes | `open docs/ux/wireframes/index.html` (on the ST-014 branch) |
| Run the guided spike on the laptop | `powershell -ExecutionPolicy Bypass -File .\run-spike.ps1 -Push` from `spike\` |
