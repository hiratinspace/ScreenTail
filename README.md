# ScreenTail

ScreenTail turns a remote-support session into a ticket note. A Windows app records what an MSP technician does during a session (clicks, screenshots and spoken narration), removes sensitive content on the device, and drafts the note. The technician reviews and edits the draft, then publishes it to their PSA and documentation tools.

**Status:** in development and not yet released. There is no installer or supported build yet, and nothing here is ready for production use or customer data.

## Repository layout

| Path | What |
|---|---|
| `client/` | Windows app: capture service, UI, and shared logic (.NET 10, WPF) |
| `backend/` | API (ASP.NET Core, .NET 10) |
| `web/` | Admin web app (React, TypeScript) |
| `shared/` | Session schema and design tokens |
| `research/` | Drafting prompts and evaluation (Python) |
| `docs/` | Architecture decisions, UX and design notes |

## Building and testing

You need the .NET 10 SDK. The client builds on any OS. Its core tests run anywhere; the Windows-specific parts run only on Windows.

```
dotnet build client/ScreenTail.sln
dotnet test client/ScreenTail.sln
dotnet test backend/ScreenTail.Backend.sln
```

- **Web:** Node 24 (see `.nvmrc`), then `cd web && npm ci && npm test`.
- **Research:** Python 3.11+, then `cd research && pip install -r requirements-dev.txt && pytest`.

To run the app on Windows for development: `powershell -ExecutionPolicy Bypass -File scripts\windows\run-local.ps1`.

## Contributing

Development conventions are in [`Build Plan/00-Build-Agent-Guide.md`](Build%20Plan/00-Build-Agent-Guide.md). Every pull request must pass the `ci-ok` check before it can be merged.

## Security

Please do not report security problems in public issues or pull requests. Use GitHub's private vulnerability reporting instead (Security tab, then "Report a vulnerability").

## Licence

This repository is public so it can be read and reviewed. No licence is granted: all rights are reserved.
