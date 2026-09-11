# Windows test loop

ScreenTail's client is Windows-only, and it's developed on a Mac. Each change goes through three layers of checks, and each layer handles only what the one before it can't.

| Where | What runs there | Who starts it |
|---|---|---|
| **Mac (dev box)** | Editing, and unit tests for platform-neutral projects (`net8.0`, not `net8.0-windows`). Windows projects compile via `EnableWindowsTargeting` but can't run. | Build agent |
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

### Security

- A self-hosted runner executes whatever the repository's workflows tell it to. That's acceptable here because the repo is private, only its owner can open PRs, and the laptop has nothing else on it. **Never attach this runner to a public repo.**
- Autologon stores the account password as an LSA secret. Use a local account that isn't used anywhere else.
- CI screenshots the laptop's desktop. Keep personal data off it.
