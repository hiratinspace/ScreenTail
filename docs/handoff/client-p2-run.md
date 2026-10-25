# Client P2 run: handoff (as of 2026-10-25, 11:00 CDT)

The 2026-10-12 review's client P2 findings are being fixed in nine groups, one branch and one PR each.
The review text itself is private and stays on the owner's Mac (`docs/review/*.local.md`, untracked). It is
not needed to continue: each branch's commits say what was fixed, and the open items below say what is left.
Never copy review findings, or any description of how to get past a control, into this public repo.

## Where each group stands

Heads move: always `git fetch origin` and work from the branch's latest commit.

| Group | Branch | Ticket | Laptop | State | Next |
| --- | --- | --- | --- | --- | --- |
| c4 pipe | st-004-ipc-p2 | ST-004 | yes | PR #207 open, reviewed and approved, hosted checks green | merge once the laptop job is green |
| c9 sessions, input, CI | st-020-sessions-input-p2 | ST-020 | yes | PR #208 open, reviewed twice, last gap fixed | merge once all checks are green |
| c1 shell, HUD | st-070-shell-hud-p2 | ST-070 | yes | review asked for changes; fix round in progress | finish the fix round (below), re-review, open PR |
| c2 review, publish | st-075-review-publish-p2 | ST-075 | no | review asked for changes; fix round in progress | finish the fix round, open PR, let the hosted Windows run settle the UI tests |
| c3 settings, net | st-046-settings-net-p2 | ST-046 | no | review asked for changes; fix round in progress | finish the fix round, re-review, open PR |
| c5 redaction | st-042-redaction-p2 | ST-042 | yes | still being built | see the c5 rule below |
| c6 speech | st-027-speech-p2 | ST-027 | yes | built; in review | review, then fix round or PR |
| c7 store, host | st-049-store-host-p2 | ST-049 | yes | built; in review | review, then fix round or PR |
| c8 capture, detection | st-022-capture-detection-p2 | ST-022 | yes | built; in review | review, then fix round or PR |

"Laptop yes" means the PR touches hardware-gated paths, so `ci-ok` waits for the self-hosted laptop runner
(SCREENTRAIL, label `screentail-win`). If it is offline, gated PRs wait; ask the owner to turn the laptop on.

## Open items per group

- **c1:** Settings must reload when the service reconnects (and never over unsaved edits); the pill's
  re-dock must use real monitor rectangles like the on-screen check; add the Quit test
  (`QuittingWhileConnectedClosesThePillAndTheIconWithoutThrowing`, a Window on an STA thread needs no
  Application); dispose the connection even if closing the pill throws; honour the current local-only
  setting when Review opens; replace the em dashes in the pill and tray status strings; update the INV-4
  wording in IndicatorReports.cs, IpcMessages.cs and docs/ipc-contract.md to match the decision below; the
  PR body must say it needs the laptop.
- **c2:** backlog Status lines for ST-074, ST-075 and ST-078; the two STA key tests must not depend on a WPF
  Application (another test shuts one down in the same process); give the key tests a negative control;
  bound the filmstrip's dispose wait; report a refused Del exclusion; log the bookkeeping fault's type;
  apply the minutes gate to Retry only for a pending time entry.
- **c3:** the draft sender must save a paid draft before writing its audit row, and the audit row must
  accept IPv6-literal and internationalised hosts; widen the release-surface check for unguarded network
  types (target-typed and initializer construction, subclasses, raw sockets, string-aware comment
  stripping) with a self-test per shape; add a backend-suite mirror of the style-hint test (do not edit
  ci.yml); a zero or past Retry-After uses the default hold; clamp the hold on read; return the hold as
  the 402/501 Retry-After; a not-attempted outcome must not fail an outbox item; tighten the sample-window
  check; a dated ST-064 Status note.
- **c5 rule:** a redaction change must be at least as strict as main. The builder must prove it
  mechanically: run every pattern test input and a broad generated corpus through main's PatternLibrary and
  the branch's, and show zero inputs that main masks and the branch does not. Two earlier attempts loosened
  the spoken-password walk (an automated security review caught both). If the proof cannot be made,
  revert only the unproven behaviour and keep the tests.
- **c6 review focus:** speech under way when capture is suppressed (a password field or excluded app takes
  focus) or when an out-of-scope window comes to the front must be dropped, never kept; keeping it there is a
  blocker whatever the docs say. Also the idle model unload's native lifetime and the open-sentence backlog
  count.
- **c7 review focus:** the erase marker must never let the service start while the store, its key, a
  credential or the IPC token is owed; refused saves for discarded sessions must not wedge finalize; the
  outbox's wait for a first policy fetch must not deadlock, and must fit c3's outbox change.
- **c8 review focus:** whether auto-stop's grace counts from the last focus into a tool, so a technician
  working inside a tool longer than the grace is stopped, and whether this branch makes that worse (a
  regression is a blocker).

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
- Runnable tests: `dotnet client/ScreenTail.Tests/bin/Debug/net10.0/ScreenTail.Tests.dll` (xunit v3; filter
  with `-method "*Name*"` or `-class "*Class*"`). Backend: `backend/tests/ScreenTail.Api.Tests` the same way.
  Research: `python -m pytest` in `research/`.
- Format gate: `dotnet format client/ScreenTail.sln --verify-no-changes`.
- Windows-only tests (`client/ScreenTail.Tests.Windows`) compile but run only in CI.

## Follow-ups after the groups merge

- Diagnostics FramesDropped should add the pending queue's refused count (c9 with c7).
- The shell should await the review pane's DisposeAsync when leaving Review (c2 with c1).
- `bundle_sent` audit rows for the publish call and the metrics sender; the backend's log scrubber needs
  the same sensitive keys as the client's.
- The pipe start command should use the same indicator gate as auto-start; a zero-size indicator report
  could be held until a real pill report arrives.
- Audit export wording: purged frames can exceed captured ones.
- Nullable diagnostics counts need an IPC version bump and the UI side.
- The speech recorder's hearing predicate can become required once StateMachineTests passes it.
- When the run is done: update docs/STATUS.md, delete this handoff note and the pointer in CLAUDE.md.
