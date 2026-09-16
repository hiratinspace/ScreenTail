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
- **It has no PowerShell 7.** Hosted runners do, so `shell: pwsh` works there. Every laptop step runs through `powershell -ExecutionPolicy Bypass`, because the runner account's execution policy is Restricted by default and the runner invokes each step as a script file — without the flag every step dies with `UnauthorizedAccess` before running a line. `setup-test-laptop.ps1` sets the policy too, so neither depends on the other.
- **Its PowerShell reads a BOM-less `.ps1` as ANSI.** One non-ASCII character corrupts everything after it and the parser then fails somewhere unrelated, complaining about an unterminated string eighty lines away. The scripts are ASCII, and `spike-windows.yml` parse-checks them and refuses a non-ASCII byte.
- Windows PowerShell 5.1 writes a byte-order mark with `Out-File -Encoding utf8`, which corrupts `GITHUB_PATH`. Append to GitHub's files with `[System.IO.File]::AppendAllText` instead.

## The skip gate (ST-018)

`dotnet test` exits 0 when every test skips, so a green tick used to mean "nothing went wrong" rather than "the checks ran". On a machine that exists to answer questions no other machine can, those are very different claims — and on 2026-09-13 the laptop's session went away mid-afternoon, every test that needs a window started skipping, and both hardware jobs went on passing.

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

### Hardware evidence is required now

`hardware-checks` is a separate workflow, so it cannot be a `needs:` of `ci-ok`. The `Hardware evidence` job in `ci.yml` bridges them: when a PR touches the paths that need a real machine, it waits for the laptop's two jobs on the same commit and fails if they did not pass. It is part of `ci-ok`, so the one required check on `main` now covers hardware.

When `HW_RUNNER` is not `true` the job does not block. It labels the PR **`needs-hardware-evidence`** and writes into the run summary that nothing needing a real desktop, screen or input has been checked. An unverified branch should say so rather than leave it to be inferred from a workflow that quietly did not run.

### Security

- A self-hosted runner executes whatever the repository's workflows tell it to. That's acceptable here because the repo is private, only its owner can open PRs, and the laptop has nothing else on it. **Never attach this runner to a public repo.**
- Autologon stores the account password as an LSA secret. Use a local account that isn't used anywhere else.
- CI screenshots the laptop's desktop. Keep personal data off it.
