# Client P2 run: handoff (as of 2026-10-26, 00:30 UTC)

The 2026-10-12 review's client P2 findings were fixed in nine groups, one branch and one PR each. Eight are
merged; one PR (#214, c5 redaction) is reviewed, approved, up to date with main and waits only on the spare
laptop's CI job.
The review text itself is private and stays on the owner's Mac (`docs/review/*.local.md`, untracked). It is
not needed to continue. Never copy review findings, or any description of how to get past a control, into
this public repo.

## Resuming from a terminal (read this first)

1. `git fetch origin` and look at PR #214: `ci-ok` must be green on its current head.
2. If its laptop jobs (`hardware-checks` workflow) are queued or failed, the laptop is the problem, not the
   code: see "Spare laptop checklist" below, ask the owner to fix it, then re-run the failed jobs of both
   the `hardware-checks` run and the `ci` run (the `Hardware evidence` job waits 35 minutes for the laptop).
3. If main has moved since #214 was last updated, merge main into `st-042-redaction-p2` (a merge commit,
   never a rebase or force-push), build and run the tests, push, and wait for CI again.
4. On a green `ci-ok`, squash-merge #214 with the title and message in "c5 squash message" below, word for
   word.
5. Then: close out the run (see "When the run is done").

## Where each group stands

| Group | Branch | PR | Laptop | State |
| --- | --- | --- | --- | --- |
| c4 pipe | st-004-ipc-p2 | #207 | yes | merged |
| c9 sessions, input, CI | st-020-sessions-input-p2 | #208 | yes | merged |
| c2 review, publish | st-075-review-publish-p2 | #210 | yes | merged |
| c3 settings, net | st-046-settings-net-p2 | #215 | no | merged |
| c7 store, host | st-049-store-host-p2 | #213 | yes | merged |
| c1 shell, HUD | st-070-shell-hud-p2 | #211 | yes | merged |
| c6 speech | st-027-speech-p2 | #212 | yes | merged |
| c8 capture, detection | st-022-capture-detection-p2 | #216 | yes | merged |
| c5 redaction | st-042-redaction-p2 | #214 | yes | approved; main merged in at 267ed6d; hosted checks green; laptop job pending |

"Laptop yes" means `ci-ok` waits for the self-hosted laptop runner (SCREENTRAIL, label `screentail-win`).

## Spare laptop checklist (SCREENTRAIL)

Every laptop failure on 2026-10-25 was the machine, not the code. Before re-running, make sure:

- **The runner is running.** If jobs sit "queued", the runner is not connected: on the laptop, Windows+R,
  `C:\actions-runner2\run.cmd`, and wait for "Listening for Jobs". Closing that window stops the runner.
- **It does not sleep or lock.** Settings, System, Power: never sleep when plugged in.
- **Smart App Control is off.** With it on, Windows refuses freshly built files ("An Application Control
  policy has blocked this file", 0x800711C7) and no test runs. The owner turned it off on 2026-10-25.
  Check: `reg query HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy`, VerifiedAndReputablePolicyState
  must be 0x0.
- **Nothing elevated is in front, and nobody touches it.** The input-injection job clicks and types; an
  admin window in front (PowerShell, Settings, Event Viewer) swallows the injected clicks, and
  `InputHookTests.HooksComeBackForTheSecondSession` fails with an empty collection. Leave it on the plain
  desktop.

## c5 squash message

Some c5 branch commit messages quantify or give an example of masking gaps that are still open. They are
public on the branch already; keep them out of main by merging with exactly this:

- Title: `fix(privacy): the spoken-password walk masks more, never less, than before, and a login screen is recognised in more wordings (#214)`
- Message:

  ```
  The spoken-password walk carries on through more clause and filler words after a cue and masks through
  to the secret where it used to stop short. Wherever the old walk masked, the new one starts at the same
  word and masks at least as far; it only acts where the old one masked nothing. The login-screen
  heuristic recognises more wordings and keeps every word it had. SpokenPasswordFloorTests keeps a frozen
  copy of the old walk and fails on any character it masks that the live library does not.

  Refs: ST-042
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  ```

## When the run is done

- Update `docs/STATUS.md` (it is a 2026-09-28 snapshot), delete this handoff note and its pointer in
  CLAUDE.md, in one docs PR.
- The next milestone is M1, the first real end-to-end session on the laptop: `docs/dev/first-draft-end-to-end.md`.

## Decisions waiting for the owner (fail-closed defaults are in place)

- #211: the pill and tray say "Paused: ..." with a colon, not the spec's em dash. Confirm or give wording.
- #216: a session inside a remote tool never times out while the tool stays in front (main stopped it after
  the grace). Should there be an input-idle limit?
- #216: elevation is relative to the service's own level; a service started elevated with UAC on would
  capture elevated windows. Add a token elevation-type check?
- #213: drafts, publishes and metrics wait for the first policy fetch with a token.
- #215: with no backend configured, an outbox item fails after a day rather than waiting quietly.

## Lessons from merging (keep for later runs)

- Only one WPF Application can exist per process, and once it shuts down no XAML component loads again.
  `PrivacyViewModelTests` owns it and runs last through `WpfApplicationOwner` and the collection orderer in
  `ScreenTail.Tests.Windows/AssemblyInfo.cs`. A new test that makes an Application must join that collection.
- A WPF items control with no presentation source makes no item containers: host it in a hidden
  `HwndSource` (`Review/UiThread.Hosted`).
- `Hardware evidence` waits 35 minutes for the laptop. Cancel stale queued laptop runs of PRs that will be
  updated anyway, so the PR being merged is not starved.
- One re-run per suspected flake: `AuditExportOverThePipeTests` timed out once on the hosted runner and
  passed on re-run.

## Decisions already taken by the owner

- **INV-4, hidden pill (2026-10-25, owner delegated, "go with the most practical"):** keep c1's reading.
  Between sessions only, a technician who hides the pill is choosing tray-only, so the UI reports a
  zero-size indicator and the next start is accepted. Once a session runs the pill is forced back and must
  really be on a monitor, or reports stop and the service suppresses. Rejected: forcing the pill before any
  start (slower, needs a new round trip, no gain), main's behaviour (one Hide refused every later start), and
  removing Hide.
- **Validator option A** (2026-10-21): the server refuses credential-shaped values only.

## How the run works

- One branch per group; never base new work on main. Commit and push after every finding:
  `git push origin HEAD:<branch>`, fast-forward only, never force.
- Never discard work you did not make (no `checkout -- .`, `reset --hard`, `clean`, `stash drop`).
- Test first: a regression test that fails for the right reason, then the fix; a tests-only change must go
  red under a deliberate mutation, which you restore.
- Fail closed where an invariant is involved; nothing new on hot paths; no content in logs.
- Commits: conventional subject in plain English, plain-prose body, no em dashes anywhere, no exploit
  detail, footer lines `Refs: ST-###` and a `Co-Authored-By:` line. PRs are squash-merged.
- After each group is built, an adversarial reviewer reads the diff and the code around it, runs the tests,
  sabotages the new tests, and answers approve or changes. Only approved groups get a PR.
- Merge one PR at a time, only on a green `ci-ok`, rebasing the next one on the new main. Never merge on red.
  c1 and c9 both edit `.github/workflows/ci.yml`: the second to merge needs a rebase.
- Never loosen what protects the self-hosted runner (triggers, permissions, runner labels, fork conditions,
  the `only()` filter, the hardware gate). Skip budgets are full (hosted 10 of 10, laptop capabilities 4 of
  4): a new Windows test must run, not skip.
- Don't make product decisions alone: take the fail-closed default, say so in the PR, and list it for the
  owner.

## Toolchain (Linux cloud box)

- .NET 10 SDK: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0`, then
  `$HOME/.dotnet/dotnet`. The whole client solution compiles off Windows: `dotnet build client/ScreenTail.sln`.
  Where dot.net is blocked by the proxy, `apt-get install -y dotnet-sdk-10.0` works; that SDK band flags a
  CA1869 in ScreenTail.Service that CI's does not, so build locally with `-p:NoWarn=CA1869` and never commit
  a change for it.
- As root, five `EraseEverythingTests` fail (a delete cannot be refused to root); they pass as a normal user.
- Runnable tests: `dotnet client/ScreenTail.Tests/bin/Debug/net10.0/ScreenTail.Tests.dll` (xunit v3; filter
  with `-method "*Name*"` or `-class "*Class*"`). Backend: `backend/tests/ScreenTail.Api.Tests` the same way.
  Research: `python -m pytest` in `research/`.
- Format gate: `dotnet format client/ScreenTail.sln --verify-no-changes`.
- Windows-only tests (`client/ScreenTail.Tests.Windows`) compile but run only in CI.

## Follow-ups after the groups merge

- Diagnostics FramesDropped should add the pending queue's refused count (c9 with c7).
- The shell should await the review pane's DisposeAsync when leaving Review (c2 with c1).
- CaptureHost should pass its logger to PublishCommands so bookkeeping faults are logged (c2 with c7).
- `bundle_sent` audit rows for the publish call and the metrics sender; the backend's log scrubber needs
  the same sensitive keys as the client's.
- The pipe start command should use the same indicator gate as auto-start; a zero-size indicator report
  could be held until a real pill report arrives.
- Audit export wording: purged frames can exceed captured ones.
- Nullable diagnostics counts need an IPC version bump and the UI side.
- The speech recorder's hearing predicate can become required once StateMachineTests passes it.
- When the run is done: update docs/STATUS.md, delete this handoff note and the pointer in CLAUDE.md.
