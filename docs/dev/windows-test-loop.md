# Windows test loop

ScreenTail's client is Windows-only, and it's developed on a Mac. Each change goes through three layers of checks, and each layer handles only what the one before it can't.

| Where | What runs there | Who starts it |
|---|---|---|
| **Mac (dev box)** | Editing, and unit tests for platform-neutral projects (`net10.0`, not `net10.0-windows`). Windows projects compile via `EnableWindowsTargeting` but can't run. | Build agent |
| **GitHub-hosted Windows runner** | Build, all tests, and automated checks on a Windows VM for every pull request. Catches build breaks and crashes. Its performance numbers don't count. | Automatic on every PR |
| **Spare laptop (self-hosted runner)** | The same checks on real hardware, plus unattended runs that inject typing and clicks (latency, performance budgets). | Automatic on every PR once `HW_RUNNER=true`; the agent can also trigger it with `gh workflow run` |
| **A person at a Windows machine** | Anything that needs judgement or another machine: RDP or ScreenConnect sessions, real typing, the look and feel of the UI, usability. | You, via the ticket's guided script (e.g. `spike/run-spike.ps1`) |

## Setting up the spare laptop

1. Get a runner registration token (valid for 1 hour). Either the build agent creates one, or you go to GitHub > Settings > Actions > Runners > New self-hosted runner.
2. On the laptop, signed in as the account that will stay signed in:
   ```powershell
   git clone https://github.com/hiratinspace/ScreenTail.git
   cd ScreenTail\scripts\windows
   powershell -ExecutionPolicy Bypass -File .\setup-test-laptop.ps1 -Token <TOKEN>
   ```
3. Do the manual steps the script prints: automatic sign-in, no lock screen, plugged in with the lid open, and use the laptop for testing only.
4. The build agent checks that the runner is online (`gh api repos/hiratinspace/ScreenTail/actions/runners`) and runs `gh variable set HW_RUNNER --body true`.

### Why the runner isn't a Windows service

Services run in session 0, which has no desktop. There, low-level hooks never fire, UI Automation sees nothing, and screen captures come back empty. The runner therefore starts from the Startup folder inside the signed-in session. That's also why the laptop needs automatic sign-in and no lock screen.

### What a self-hosted runner can't do

Both of these cost a failed run before the real work started, so jobs that run on the laptop differ from the hosted ones:

- **It isn't an administrator,** so `actions/setup-dotnet` fails: it installs into `C:\Program Files\dotnet`, which the runner account can't write to. The laptop job uses the SDKs that `setup-test-laptop.ps1` installed and fails with a clear message if they're missing.
- **It has no PowerShell 7.** Hosted runners do, so `shell: pwsh` works there. Every laptop step runs through `powershell -ExecutionPolicy Bypass`, because the runner account's execution policy is Restricted by default and the runner invokes each step as a script file; without the flag every step dies with `UnauthorizedAccess` before running a line. `setup-test-laptop.ps1` sets the policy too, so neither depends on the other.
- **Its PowerShell reads a BOM-less `.ps1` as ANSI.** One non-ASCII character corrupts everything after it and the parser then fails somewhere unrelated, complaining about an unterminated string eighty lines away. The scripts are ASCII, and the `Windows scripts parse` job in `ci.yml` parse-checks them with Windows PowerShell 5.1 and refuses a non-ASCII byte.
- Windows PowerShell 5.1 writes a byte-order mark with `Out-File -Encoding utf8`, which corrupts `GITHUB_PATH`. Append to GitHub's files with `[System.IO.File]::AppendAllText` instead.

### Before a laptop run

Every laptop failure during the client P2 merges (2026-10-25) was the machine, not the code. Before re-running a failed or stuck laptop job, check these:

- **The runner is running.** If laptop jobs sit "queued", no runner is connected. The runner starts from the Startup folder when the account signs in, in a minimized window whose title ends in `run.cmd`; closing that window stops it. To start it by hand: Windows+R, `C:\actions-runner2\run.cmd`, and wait for "Listening for Jobs". Jobs queued for an offline runner expire after about a day, so re-run them once it is back.
- **It is still registered.** GitHub deletes a runner that has been offline for 14 days. It happened on 2026-10-19, and the runner was registered again into `C:\actions-runner2` with `setup-test-laptop.ps1 -RunnerDir C:\actions-runner2`, because the script skips registration in a folder that already holds a `.runner` file. A 404 from the registration step means a mistyped token.
- **It does not sleep or lock.** Settings, System, Power: never sleep when plugged in.
- **Smart App Control is off.** With it on, Windows refuses freshly built files ("An Application Control policy has blocked this file", 0x800711C7) and no test runs. It was turned off on 2026-10-25. To check: `reg query HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy`; `VerifiedAndReputablePolicyState` must be `0x0`.
- **Nothing elevated is in front, and nobody touches it.** The input-injection job clicks and types. An administrator window in front (PowerShell, Settings, Event Viewer) swallows the injected clicks, and `InputHookTests.HooksComeBackForTheSecondSession` fails with an empty collection. Leave the laptop on the plain desktop.
- **The demo is not running.** The laptop is also where the demo runs (`docs/dev/demo.md`); a test job that starts during a demo takes over the keyboard, mouse and screen. Close the runner's window for the demo, and it comes back at the next sign-in. The other way round, a job that starts while the demo is up fails at its first step (`scripts/windows/check-demo-stopped.ps1`) with one line: run `scripts\windows\demo\stop.cmd`, then re-run the job. A ScreenTail process the demo did not start is named with its process id instead, to be closed by hand, since `stop.cmd` stops only what its state file records. The skip gate still runs after that failure, as it runs after any, and adds a second error, "No TRX reports matched": no tests ran, so there is no report. That second line is the first one's consequence, not another fault.

## The skip gate (ST-018)

`dotnet test` exits 0 when every test skips, so a green tick used to mean "nothing went wrong" rather than "the checks ran". On a machine that exists to answer questions no other machine can, those are very different claims, and on 2026-09-13 the laptop's session went away mid-afternoon, every test that needs a window started skipping, and both hardware jobs went on passing.

Three things close that hole.

**Each test app is run directly**, not through `dotnet test`, at the path `dotnet msbuild -getProperty:TargetPath` reports, with xUnit's `-result-trx`. `dotnet test` does not forward that option and rejects the Microsoft.Testing.Platform equivalent, which this project has no extension package for.

**`scripts/windows/check-skips.ps1` counts what did not execute** and compares it with `client/ScreenTail.Tests.Windows/skip-baseline.json`. Every skipped test is named in the log and the job summary with its reason. The same assembly runs in three jobs with different permissions, so each has its own number:

| Job | Budget | Why |
|---|---|---|
| `hosted` (windows-latest, both test projects) | 10 | Four input-injection tests need `SCREENTAIL_ALLOW_INPUT_INJECTION`; six performance tests refuse to enforce a budget on a shared cloud VM |
| `laptop-capabilities` | 4 | The four input-injection tests belong to the job below |
| `laptop-input` | 0 | Everything runs |

All three were measured on 2026-09-16, not guessed.

**The budgets may only go down.** A pull request that raises one fails the `The skip budget has not been raised` check in `ci.yml`. Lowering needs no ceremony, and a job says so whenever it skips fewer than its budget.

**The path filter lives in two places and they have to agree exactly**: `hardware-checks.yml`'s `paths:`
and the `hardware` flag in `ci.yml`'s change detection, which for this one flag uses `only` rather than
`flag` so the shared files are not ORed in. If the filter says no and the flag says yes, the gate waits
five minutes for evidence that is never coming and then says so. If the reverse, a change reaches `main`
without the laptop seeing it.

Both directions happened on 2026-09-16. The speech pipeline, which opens a real microphone, was in
neither list. Then the shared files (`ci.yml` itself among them) were in the flag and not in the
filter, so the pull request fixing the first problem failed its own gate. They were brought into line by
narrowing the flag rather than widening the filter, because widening meant a one-line workflow edit took
over the laptop's keyboard, and the hosted Windows job builds and tests everything either way.

### Hardware evidence is required now

`hardware-checks` is a separate workflow, so it cannot be a `needs:` of `ci-ok`. The `Hardware evidence` job in `ci.yml` bridges them: when a PR touches the paths that need a real machine, it waits for the laptop's two jobs on the same commit and fails if they did not pass. It is part of `ci-ok`, so the one required check on `main` now covers hardware.

**It runs on pull requests only.** `hardware-checks.yml` has no `push` trigger, so on a merge the gate
would wait for laptop jobs that are never created. It did exactly that for three merges on 2026-09-16:
the pull requests were green, the merge commits went red thirty-five minutes later, and nothing was wrong
with any of them. Branch protection means every commit on `main` arrived through a pull request where the
gate already ran, so re-checking the merge buys nothing.

When `HW_RUNNER` is not `true` the job does not block. It labels the PR **`needs-hardware-evidence`** and writes into the run summary that nothing needing a real desktop, screen or input has been checked. An unverified branch should say so rather than leave it to be inferred from a workflow that quietly did not run.

**When several pull requests wait for the laptop,** it runs their jobs one at a time and `Hardware evidence` gives up after thirty-five minutes. Cancel the queued laptop runs of pull requests that will be updated anyway, so the one about to merge is not starved. After the laptop is fixed, re-run the failed jobs of both the `hardware-checks` run and the `ci` run.

**One re-run per suspected flake.** A test that fails once on a runner and passes on one re-run is noted and watched, as `AuditExportOverThePipeTests` was when it timed out once on the hosted runner. A second failure is a bug to fix, not a reason to re-run again.

### Security

- A self-hosted runner executes whatever the repository's workflows tell it to, inside a signed-in desktop session. This line used to read "never attach this runner to a public repo", written when the repository was private. **The repository is public now**, because hosted runner minutes are free for public repositories and are not for private ones, and for several days the rule was simply broken: any stranger's pull request could have built and run its code on this laptop behind one "Approve and run" click. Found in the 2026-09-19 review; no fork existed and every run was the owner's.
- What keeps strangers' code off the laptop is **three repository settings, not anything in a workflow file.** A pull request runs its own copy of the workflows, so a guard written in one can be deleted by the pull request it is guarding against, and a stranger can add a new workflow that targets the laptop's label. The settings live where a pull request cannot reach:
  - *Settings → General → Pull requests:* only collaborators may open them (`pull_request_creation_policy = collaborators_only`). This is the one that matters. The repository is public to read and closed to contribute.
  - *Settings → Actions → General → Fork pull request workflows:* require approval for **all** outside contributors, not only first-time ones. If the first setting is ever loosened, nothing from a fork runs without a click.
  - *Settings → Actions → General → Actions permissions:* GitHub-owned actions only. Actions run on the laptop too.
- **Before loosening any of those, take the runner offline or set `HW_RUNNER=false`.** Accepting outside contributions and having this laptop attached are not compatible, and the second lock in the workflows (laptop jobs skip pull requests from forks) is there for the day somebody forgets, not as a substitute.
- Never click "Approve and run" on a pull request you have not read. Approval is the last barrier, and what it approves is code execution on a machine in your house.
- The remaining risk is a compromised dependency running during a build on the laptop. That was equally true when the repository was private.
- Autologon stores the account password as an LSA secret. Use a local account that isn't used anywhere else.
- **Keep the laptop signed out of everything**, the browser above all. It should hold nothing worth stealing. The review found Chrome on it signed in to GitHub as the owner, which would have turned code execution on the laptop into the owner's GitHub account.
- CI captures the laptop's desktop, and artifacts on a public repository can be downloaded by anyone signed in to GitHub. The laptop jobs delete every image before uploading and keep what is left for seven days; the verdicts are text. One artifact uploaded before this showed the owner's browser, and all of the laptop's earlier artifacts were deleted on 2026-09-19.

## The end-to-end run (ST-013)

`scripts/windows/e2e/run-e2e.ps1` takes one session through the whole loop, backend included, and prints
PASS or FAIL per step (`docs/dev/e2e.md`). CI's `End to end (Windows)` job runs it on the hosted VM with
mark-moment captures, and the laptop's input job runs it with `-InjectClicks`: real hotkeys and clicks.
Both are report-only for now. It is not a test in the skip gate's TRX, so it changes no budget. On the
laptop it needs `scripts\windows\demo\setup.cmd` to have been run once, and it refuses to run while the
demo is.

## Performance measurements

ST-031's budgets are measured in the capabilities job by `client/ScreenTail.Tests.Windows/Performance`, and the numbers go to the job summary and `measurements.txt` like the redaction figures. They take about three and a half minutes of the laptop's time. `docs/dev/performance.md` lists each budget, whether it is enforced, and the latest numbers; `scripts/windows/measure-idle.ps1` measures the real service and UI processes by hand.

## Writing Windows tests

The Windows-only suite (`client/ScreenTail.Tests.Windows`) runs its tests one at a time in one process, because they share the desktop (`AssemblyInfo.cs` says why). Some WPF rules follow from that one process:

- **Only one WPF `Application` can exist per process,** and once it shuts down no XAML component loads again. It is the fixture of the `WpfApplicationOwner` collection (`Accessibility/TheApplication.cs`), made once on a thread that pumps for the whole collection and shut down after its last test; the collection orderer in `AssemblyInfo.cs` runs that collection last. A test that needs the app's styles joins the collection, takes `TheApplication` in its constructor and runs its body with `RunAsync`. It never makes an `Application` of its own.
- **The accessibility suite (`Accessibility/`, ST-084) brings its own test windows to the front** for a few minutes of the laptop's capabilities job: it shows and activates each screen in turn. Its key presses go only to those windows (below), never to another program, but while one of them is in front a person typing on the laptop types into it, so leave the laptop alone while that job runs. (The workflow's comment that the job is safe on a machine in use predates this; it is the owner's to update, since editing that workflow turns the hardware gate on.)
- **The accessibility suite drives the keyboard without injecting input.** `Keys.Press` hands a test window's `HwndSource` the key messages the message loop would, and holds modifiers with `SetKeyboardState`, which affects the calling thread only, so no key reaches Windows' input queue or any other program's window. That is why it runs on the hosted runner. The axe-windows scan runs in its own process (`ScreenTail.Tests.AxeRunner`), because the engine skips every element of the process that runs it.
- **A WPF items control with no presentation source makes no item containers,** so a test that reads them gets nulls. Host the control in a hidden `HwndSource` (`Review/UiThread.Hosted`).
- **Once anything in the process is DPI aware, text is drawn at the screen's scale.** The laptop's screen is scaled, so OCR fixtures are drawn at a fixed 96 dpi (`OcrTests`) rather than whatever the process happens to be using.
- **The skip budgets are full** (hosted 10 of 10, `laptop-capabilities` 4 of 4). A new Windows test must run on every job, not skip.
