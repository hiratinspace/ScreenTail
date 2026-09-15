# ScreenTail — Build Agent Execution Guide (v0.4)

This is the entry point for any human or AI build agent working on ScreenTail. Read this file first, then the UX Design Spec, then pick up the next open ticket from the Backlog.

**Package contents**
- `00-Build-Agent-Guide.md` — this file: how to work, non-negotiable invariants, execution order
- `01-UX-Design-Spec.md` — tokens, screens, states, copy, shortcuts; the single source of truth for anything a user sees
- `02-Backlog-v0.4.md` — ticketed work with IDs, dependencies, acceptance criteria and per-ticket agent briefs

---

## 1. What we are building (one paragraph)

ScreenTail is a Windows desktop app for MSP technicians. A background **capture service** notices when a remote-support window (ScreenConnect, RDP, Splashtop, TeamViewer, AnyDesk, or a browser-based remote client) is in focus, records clicks and click-triggered screenshots, transcribes the technician's microphone locally, redacts sensitive content on-device, and — after the session — asks a language model once to draft a ticket note (Problem → Steps → Result → Follow-ups). A **UI process** (tray icon, recording HUD, Review screen, Settings) lets the technician review the draft in about thirty seconds and publish it to ConnectWise Manage and Hudu. A small **web app** handles tenant signup, seats and admin policy.

---

## 2. Repository layout

```
/client            .NET 10 solution
  /ScreenTail.Core         platform-neutral capture logic: state machine, store rules, patterns, aligner, bundle (ADR-0002)
  /ScreenTail.Service      Windows host + adapters (hooks, detection, screenshots, STT, OCR, DPAPI, pipes) over Core
  /ScreenTail.UI           WPF app (shell, tray, HUD, Review, Settings, Onboarding)
  /ScreenTail.Shared       generated types, IPC contracts, tokens
  /ScreenTail.Tests        unit + integration tests for Core/Shared (net10.0, runs on any OS)
  /ScreenTail.Tests.Windows  Windows-bound tests (added when the first one is needed)
/backend           ASP.NET Core API (auth, tenants, policy, summarization broker, providers, metrics)
/web               React + TypeScript (tenant onboarding, admin dashboard)
/shared
  /schema          session.v1.json and codegen config
  /design          tokens.json (consumed by WPF and web)
/research          Python: prompts, eval harness, fixtures (golden set arrives with ST-030)
/docs
  /adr             architecture decision records and their evidence
  /security        threat model, data-flow, questionnaire (red-team reports arrive with ST-115)
  /review          ranked review findings (weaknesses.md)
  /product         market scans and idea lists — reference, never tickets
  /legal           consent guidance
  /dev             the Windows test loop
  /ux              wireframes and hi-fi assets
  /integrations    ConnectWise, Hudu notes (arrive with ST-091, ST-095)
  README.md        which document is intent, which is record, which is evidence
```

Ticket IDs are `ST-###`. Branch naming: `st-###-short-slug`. One PR per ticket unless the ticket says otherwise.

**Where state lives.** Each ticket in the backlog carries a **Status** line (Done / Partial / Open, with what remains and what it needs from the owner). That line is the source of truth; the PR that changes a ticket's state updates it. `docs/STATUS.md` is a summary for humans and is refreshed after each stretch of work, never instead of the status line.

---

## 3. Invariants — never violate, never "temporarily" bypass

An agent that finds a ticket seemingly requiring one of these to be broken must stop and open an ADR/question rather than proceed.

| ID | Invariant | Where enforced |
|---|---|---|
| INV-1 | **No unredacted frame is ever shown, exported or transmitted.** Frames enter the store with `redaction_pending: true`; only the redaction worker flips it; every read path (Review, bundle builder, export, KB publish) filters `redaction_pending = false`. | Store read API, bundle builder tests |
| INV-2 | **Raw keystrokes are never recorded.** Keyboard hooks emit categories (`typing_burst`, `shortcut`, `enter`) with counts only. | Hook layer, schema |
| INV-3 | **Nothing is published without a human click** in v1. No auto-publish path exists. | Publish panel, backend (no unauthenticated publish endpoint) |
| INV-4 | **Capture is visibly indicated** while recording (tray state + HUD unless the user explicitly chose tray-only). There is no silent-capture mode. | State machine → tray/HUD bindings |
| INV-5 | **Capture scope defaults to remote-tool windows + admin-tool allowlist.** "All windows" is opt-in and confirmed. | Registry / scope policy |
| INV-6 | **Suppression states drop data, not just hide it.** Password field, excluded app, elevated window, out-of-scope → no frames, no typing events written. | State machine sub-state `suppressed` |
| INV-7 | **The backend never persists frames.** Summarization holds frames in memory for one request. Tests assert storage size unchanged. | Backend summarization endpoint |
| INV-8 | **Local-only mode means zero egress** except user-initiated publish to PSA/doc hosts. Enforced by an HTTP allowlist, not by convention. | Egress guard |
| INV-9 | **Only the technician's microphone is captured** in v1. End-user audio requires the v1.2 consent workflow. | STT pipeline |
| INV-10 | **Logs and metrics contain no content** (no window titles, transcript, OCR text, note text, company or ticket text). | PII scrubber, metrics schema |
| INV-11 | **Admin policy fields are enforced on the client**, not just displayed. Locked fields cannot be changed locally. | Policy sync |
| INV-12 | **Raw data has a retention limit** (default 7 days) and a "delete everything" path that also removes tokens. | Retention purge |

---

## 4. How to execute a ticket

1. **Read** the ticket's *Agent brief → Read* list before writing code. UI tickets always reference a section of `01-UX-Design-Spec.md`; that section is authoritative for layout, copy, states and shortcuts.
2. **Check blockers.** Do not start a ticket whose `Blocked By` IDs are not merged. If a blocker is partially done, coordinate, don't stub around it.
3. **Write** only within the paths listed in *Agent brief → Write* unless a change elsewhere is unavoidable; if so, call it out in the PR.
4. **Verify** with the commands in *Agent brief → Verify*. Every acceptance criterion must map to a test, a recorded manual check, or a screenshot in the PR.
5. **Definition of Done** (all required):
   - All acceptance criteria checked in the PR description, and the ticket's **Status** line in the backlog updated in the same PR
   - Unit tests for new logic; integration test where the ticket touches the store, IPC or a provider
   - No new warnings from analyzers; formatting applied
   - UI tickets: screenshots in both themes at 1366×768 and 1920×1080; keyboard path demonstrated
   - Privacy-touching tickets: state which invariants you exercised and how
   - Docs updated (`/docs`, schema, tokens) when contracts change
6. **When the ticket is ambiguous**, prefer the option that (a) keeps invariants, (b) matches the UX spec, (c) is simplest. Record the choice in the PR. If the ambiguity changes architecture, write an ADR instead of guessing.
7. **Never expand scope** inside a ticket. New ideas become new tickets.

---

## 5. Coding conventions (summary)

- **Client:** .NET 10 (LTS), C# 14, nullable enabled, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`. WPF with MVVM (CommunityToolkit.Mvvm). No business logic in views. Platform-neutral logic lives in `ScreenTail.Core` (ADR-0002). Async all the way; capture threads never await UI. *(Amended 2026-09-11: .NET 8 support ends November 2026; see ADR-0001 finding 1.)*
- **IPC:** named pipe, length-prefixed JSON messages, versioned `IpcContractVersion`. Commands from UI → service; events from service → UI. Every command is authenticated with the session token.
- **Store:** SQLite + SQLCipher via a repository interface; migrations are numbered SQL files; no ad-hoc SQL in view models.
- **Backend:** ASP.NET Core minimal APIs on .NET 10, EF Core, Postgres. Providers behind `IPsaProvider` / `IDocProvider`. All external calls have timeouts and typed errors.
- **Web:** React 18 + TypeScript, Vite, CSS variables from `/shared/design/tokens.json`. No component library that fights the tokens.
- **Research:** Python 3.11, prompts as versioned `.md` files, eval harness runs in CI on prompt changes.
- **Tests:** xUnit (client/backend), Vitest (web), pytest (research). Golden sessions live in `/research/fixtures`.
- **Commits:** conventional commits (`feat:`, `fix:`, `test:`, `docs:`), ticket ID in the footer.

---

## 6. Execution order

Work follows the **ordered plan in `02-Backlog-v0.4.md` Part C** (phases A–E, replacing the v0.4 sprint plan on 2026-09-15). Work a phase top to bottom; when a ticket is waiting on the owner, take the next unblocked one, in the same phase first. Part D records every amendment to the v0.4 dependencies and why. Two agents/engineers split as:

- **Agent A (capture, privacy, intelligence):** ST-020–ST-049, ST-060–ST-069
- **Agent B (infra, design, UI, integrations, release):** ST-001–ST-019, ST-050–ST-059, ST-070–ST-129

Milestone gates:
- **M1** at the end of Phase B — real Review UI shows real drafts from real sessions; eval harness reports quality
- **M2** at the end of Phase C — publish to ConnectWise + Hudu works end-to-end behind human review
- **M3** at the end of Phase D — signed installer, onboarding, policy, docs; pilot begins
- **M4** — Phase E, v1.1 tickets

---

## 7. Design quality bar (what "best UI/UX" means here)

Agents building UI are held to these, in this order of priority:

1. **Correct state, always visible.** Recording / paused / suppressed / draft-ready / offline are never ambiguous.
2. **30-second review.** Scan → fix → publish with keyboard only. Anything that slows that loop is a bug.
3. **Trust through transparency.** Every screen that touches data shows what was captured, redacted, purged and sent.
4. **Calm density.** Compact layouts, no marketing gloss, no motion longer than 180 ms, no modal during recording.
5. **Consistency over novelty.** Use tokens and components; do not invent a new control if one exists.
6. **Accessible by default.** WCAG 2.1 AA contrast, full keyboard path, automation names on every interactive element.
