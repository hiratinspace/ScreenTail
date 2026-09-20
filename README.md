# ScreenTail

ScreenTail is a Windows desktop app for MSP technicians. It notices when a remote-support window is in focus, captures clicks, screenshots and the technician's narration, redacts sensitive content on the device, and drafts the ticket note (Problem → Steps → Result → Follow-ups). The technician reviews the draft in about 30 seconds and publishes it to ConnectWise Manage and Hudu. A small web app handles tenant signup, seats and admin policy.

Start with [`Build Plan/00-Build-Agent-Guide.md`](Build%20Plan/00-Build-Agent-Guide.md): it has the invariants, conventions and execution order. The UX spec and the backlog sit next to it.

## Repository layout

| Path | What | Stack |
|---|---|---|
| `client/` | Capture service, WPF UI, platform-neutral core, shared contracts, tests | .NET 10, WPF, CommunityToolkit.Mvvm, xUnit v3 |
| `backend/` | API: auth, tenants, policy, summarization broker, providers | ASP.NET Core minimal APIs on .NET 10 |
| `web/` | Tenant onboarding and admin dashboard | React 18, TypeScript, Vite, Vitest |
| `shared/` | Session schema (`schema/`) and design tokens (`design/`) | JSON |
| `research/` | Prompts, evaluation harness, fixtures | Python 3.11+, pytest, ruff |
| `docs/` | ADRs, UX, security, product research | Markdown |

## Local setup

### Client

On Windows you can build, test and run everything. On macOS or Linux you can build everything, and the Core and Shared tests run ([ADR-0002](docs/adr/0002-platform-neutral-core.md)).

1. Install the .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10` (or see [dot.net](https://dot.net)).
2. `git clone https://github.com/hiratinspace/ScreenTail.git` and `cd ScreenTail`.
3. `dotnet build client/ScreenTail.sln`
4. `dotnet test client/ScreenTail.sln`
5. Windows only, to run it: `powershell -ExecutionPolicy Bypass -File scripts\windows\run-local.ps1`.

   It publishes the capture service and the UI into one directory and starts both. **That directory is
   not a convenience.** ADR-0003's handshake asks who is on the other end of the pipe; a release answers
   with an Authenticode signature, and an unsigned development build has none, so the rule falls back to
   requiring the peer's executable to sit beside ours. Running each project on its own with `dotnet run`
   puts them in separate `bin` folders and fails that check in both directions, leaving a tray icon stuck
   on "service unavailable" for a reason recorded nowhere a person would look.

   The UI starts in the notification area rather than opening a window: the tray icon and the recording
   pill are the whole interface until there is a draft to review. Press **Ctrl+Alt+R** to start a session
   from the window in front, or bring a remote-support tool to the foreground and detection starts one.
   Ctrl+Alt+P pauses and resumes, Ctrl+Alt+S stops and drafts, Ctrl+Alt+M marks a moment.

### Backend

1. Install the .NET 10 SDK (as above).
2. `dotnet test backend/ScreenTail.Backend.sln`
3. `dotnet run --project backend/src/ScreenTail.Api`, then open `http://localhost:5000/health`.
4. Container: `docker build -f backend/Dockerfile -t screentail-api .` and `docker run --rm -p 8080:8080 screentail-api`, then open `http://localhost:8080/health`.

### Web

Needs Node 24 (see `.nvmrc`). Run `cd web && npm ci && npm run dev`. The same checks as CI: `npm run lint && npm run typecheck && npm test && npm run build`.

### Research

Needs Python 3.11+. Run `cd research && python -m venv .venv && . .venv/bin/activate && pip install -r requirements-dev.txt && ruff check . && pytest`.

## Quality gates

`.github/workflows/ci.yml` runs on every pull request, and only for the areas the PR touches:

- **Client** (on `windows-latest`): `dotnet format --verify-no-changes`, a Release build with analyzers and warnings as errors, and tests.
- **Backend:** the same checks, plus a container build.
- **Web:** ESLint with zero warnings allowed, `tsc`, Vitest and a production build.
- **Research:** `ruff check`, `ruff format --check` and pytest.
- **Commits:** each one must be a conventional commit with a `Refs: ST-###` footer.

`ci-ok` summarizes all of them and is the required check for merging into `main`.

## Working on a ticket

- Branch `st-###-short-slug` from `main`; one PR per ticket.
- Conventional commits (`feat:`, `fix:`, `docs:`, `test:`, `ci:` …) ending with `Refs: ST-###`.
- Windows-bound work is verified as described in `docs/dev/windows-test-loop.md`.
- Decisions that change the architecture get an ADR in `docs/adr/`.
