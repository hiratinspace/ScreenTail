# Client P2 run: handoff (as of 2026-10-25, 16:00 CDT)

The 2026-10-12 review's client P2 findings are being fixed in nine groups, one branch and one PR each.
The review text itself is private and stays on the owner's Mac (`docs/review/*.local.md`, untracked). It is
not needed to continue: each branch's commits say what was fixed, and the open items below say what is left.
Never copy review findings, or any description of how to get past a control, into this public repo.

## Where each group stands

Heads move: always `git fetch origin` and work from the branch's latest commit.

| Group | Branch | PR | Laptop | State | Next |
| --- | --- | --- | --- | --- | --- |
| c4 pipe | st-004-ipc-p2 | #207 | yes | merged | none |
| c9 sessions, input, CI | st-020-sessions-input-p2 | #208 | yes | merged | none |
| c2 review, publish | st-075-review-publish-p2 | #210 | yes | merged | none |
| c3 settings, net | st-046-settings-net-p2 | #215 | no | merged | none |
| c7 store, host | st-049-store-host-p2 | #213 | yes | merged | none |
| c1 shell, HUD | st-070-shell-hud-p2 | #211 | yes | merged | none |
| c6 speech | st-027-speech-p2 | #212 | yes | reviewed twice and approved; main merged in (fe6c07a); hosted checks green | blocked on the laptop (below); re-run its laptop jobs and Hardware evidence, merge on green |
| c8 capture, detection | st-022-capture-detection-p2 | #216 | yes | reviewed and approved | after #212: merge main in, CI, merge |
| c5 redaction | st-042-redaction-p2 | #214 | yes | strictness proof done, security review approved | last: merge main in, CI, merge. Write the squash message by hand (see c5 below) |

"Laptop yes" means `ci-ok` waits for the self-hosted laptop runner (SCREENTRAIL, label `screentail-win`).

**Laptop blocker (2026-10-25):** Windows Application Control on SCREENTRAIL started refusing freshly
built files ("An Application Control policy has blocked this file", 0x800711C7): `ScreenTail.UI.dll` on
one #211 run, then `ScreenTail.Service.exe` twice on #212, so no test ran. The owner has been asked to turn
Smart App Control off (Windows Security, App & browser control) or find the WDAC policy behind it. Until
then every laptop-gated PR fails before its tests. It is not a code fault; do not change code for it.

## Open items per group

- **c6, c8, c5:** nothing left in code. Each PR body lists its decisions and follow-ups.
- **c5 squash message:** some branch commit messages quantify or give an example of masking gaps that are
  still open. They are public on the branch already; keep them out of main by writing the squash commit
  message by hand.
- **Merge order and why:** c6 before c8 (c8 closes a coordinator race c6's review noted), c5 last.

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

## Decisions waiting for the owner (fail-closed defaults taken)

- #211: the pill and tray say "Paused: ..." with a colon, not the spec's em dash. Confirm or give wording.
- #216: a session inside a remote tool now never times out while the tool stays in front (main stopped it
  after the grace). Should there be an input-idle limit?
- #216: elevation is relative to the service's own level; a service started elevated with UAC on would
  capture elevated windows. Add a token elevation-type check?
- #213: drafts, publishes and metrics wait for the first policy fetch with a token.
- #215: with no backend configured, an outbox item fails after a day rather than waiting quietly.

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
