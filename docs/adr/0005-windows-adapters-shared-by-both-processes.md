# ADR-0005: A Windows adapter library shared by the service and the UI

- **Status:** Accepted 2026-09-16 (ST-085).
- **Extends:** [ADR-0002](0002-platform-neutral-core.md) (platform-neutral core), [ADR-0003](0003-process-hosting-and-ipc.md) (two processes, one pipe).
- **Invariants:** INV-1, INV-4, INV-8.

## Context

ADR-0002 put platform-neutral logic in `ScreenTail.Core` and said Windows-only code sits behind thin
adapters. Every adapter went into `ScreenTail.Service`, which was right while the service was the only
process that needed one: the UI drew windows and read nothing from Windows that WPF did not already give
it.

ST-085 changed that. Before the UI writes the session token to the pipe it has to verify that the process
answering really is the capture service — ADR-0003's rule read the other way round, and the half that
`docs/review/weaknesses.md` P0-2 found unenforced. That check is Authenticode, which is Windows, and it
lives in `WindowsServerVerifier` next to the client-side `WindowsClientVerifier` that the service uses.
Both sit on the same `Authenticode` helper.

So the UI needed a Windows adapter, and the only one that existed was inside the project it must not
reference.

## Decision

**A fourth client project, `ScreenTail.Platform` (`net10.0-windows`), holds the Windows adapters that both
processes need.** It starts with the four that ST-085 required: `Authenticode`, `WindowsClientVerifier`,
`WindowsServerVerifier` and `WindowsPipeFactory`. `ScreenTail.Service` and `ScreenTail.UI` both reference
it; `ScreenTail.Core` does not and must not.

The rule for what belongs here is narrow: **an adapter moves into `ScreenTail.Platform` when the second
process needs it, and not before.** Anything one process needs alone stays where it is. The hooks, the
screen capture, the OCR engine, the store and the focus watcher are all service-only and stay in
`ScreenTail.Service`.

## Why not the alternatives

| Option | Why not |
|---|---|
| `ScreenTail.UI` references `ScreenTail.Service` | Pulls the hooks, the store, the OCR engine and the whole capture engine into the UI process for the sake of one signature check. The UI is the process that runs all day in front of a customer; the smaller its surface, the better. It also puts a store handle in a process that has no business opening one, which is how INV-1's read-path filtering would end up with two owners. A test in `ReleaseSurfaceTests` now forbids the reference outright. |
| Duplicate `Authenticode` in the UI | Two copies of a signature check is one copy that will be fixed and one that will not. This is the code that decides whether a stranger gets the session token. |
| Move the adapters into `ScreenTail.Core` | Breaks ADR-0002. Core is `net10.0` and runs its tests on the build machine, which is a Mac; a Windows dependency there costs the whole platform-neutral test suite. |
| Put them in `ScreenTail.Shared` | Shared is generated contract types, referenced by Core. Making it Windows-only would drag Core to Windows by the same argument as above. |

## Consequences

- Two more project references and one more project in CI's client build. No new packages.
- `WindowsClientVerifierTests` and `WindowsServerVerifierTests` moved namespace with the code; their
  content is unchanged.
- `ScreenTail.Platform` is in the `ReleaseSurfaceTests` sweep, so the "no listeners, no `#if DEBUG`"
  rules apply to it like every other client project.
- The hardware-checks path filter includes it, because a change to a verifier is a change the laptop
  should see.
- If a third process ever appears, this is where its shared adapters go. If one never does, this project
  stays at four files, which is the right size for it.
