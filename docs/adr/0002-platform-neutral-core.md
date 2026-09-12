# ADR-0002: A platform-neutral core project for client logic

- **Status:** Proposed (ST-002). Accepted when the ST-002 PR merges.
- **Date:** 2026-09-11
- **Deciders:** project owner; build agent

## Context

Guide §2 lays out the client as `ScreenTail.Service` (everything capture-related), `ScreenTail.UI`, `ScreenTail.Shared` and `ScreenTail.Tests`. The service needs Windows APIs (hooks, UI Automation, screen capture, DPAPI, named pipes), so it targets `net10.0-windows`. A test project that references it must target Windows too, and then no client test can run on the macOS machine where the code is written. Every red-green cycle would cost a CI round trip of about 5 minutes.

Most of the service's logic doesn't need Windows at all: the session state machine (ST-020), store and retention rules behind interfaces (ST-005, ST-044), the redaction pattern engine (ST-042), timeline alignment (ST-028), the bundle builder (ST-060), time-entry calculation (ST-066), outbox retry policy (ST-064) and the audit hash chain (ST-045).

## Decision

1. Add **`client/ScreenTail.Core`** (`net10.0`) for platform-neutral client logic. `ScreenTail.Service` becomes the Windows host: thin adapters (hooks, UIA, capture, DPAPI key provider, pipe server) over Core's interfaces.
2. **`ScreenTail.Tests`** targets `net10.0`, covers Core and Shared, and runs on any OS.
3. Windows-bound tests (adapters, WPF view models that need a dispatcher) go into **`ScreenTail.Tests.Windows`** (`net10.0-windows`), created when the first one is needed. CI runs both test projects on `windows-latest`.
4. A guard test (`Architecture/PlatformNeutralityTests`) fails if Core or Shared gains a target platform or references a Windows-only assembly.

## Consequences

- The backlog's *Agent brief → Write* paths name `ScreenTail.Service/<Area>/…`. The platform-neutral parts of those tickets go to `ScreenTail.Core/<Area>/…` with the same folder names; the Windows adapters stay under `ScreenTail.Service/<Area>/…`. PRs call out the mapping.
- Invariants that are enforced "in the store read API" or "in the state machine" (INV-1, INV-6) are enforced in Core, so their tests run everywhere.
- One more project to maintain, and some interfaces that exist only to separate the Windows adapters from Core. That's the price of a fast test loop.

## Alternatives considered

- **Keep the guide's layout, run all tests on Windows only.** Simple, but every test cycle goes through CI.
- **Multi-target ScreenTail.Service (`net10.0;net10.0-windows`).** Keeps one project, but fills it with `#if WINDOWS` and makes the boundary easy to break without anyone noticing.
