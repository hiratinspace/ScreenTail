# ScreenTail — Engineering Backlog v0.4 (agent-executable)

**Read first:** `00-Build-Agent-Guide.md` (invariants INV-1…INV-12, conventions) and `01-UX-Design-Spec.md` (referenced below as **Spec §x**).
**Format:** every ticket has an ID, epic, priority, estimate, dependencies by ID, acceptance criteria, and an *Agent brief* (Read / Write / Verify).
**Totals:** 293 pts · 15 sprints solo · 8 with two agents. Sprint plan in Part C.

Priority: `Urgent` = critical path to M1 or upstream of it · `High` = M2/M3 · `Medium` = M3 polish · `Low` = v1.1+

---

## Phase 0 — Foundation & Infrastructure (Epic: INFRA)

---
### **ST-001 · Spike: validate .NET 8 client stack (hooks + UIA + WPF + whisper.net + OCR)**
- **Epic/Feature:** INFRA
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
Throwaway solution proving a .NET 8 background process can run low-level hooks, query UI Automation (FlaUI), capture/downscale screenshots and run whisper.net concurrently with no perceptible input lag, while a separate WPF process shows a tray icon and a capture-excluded overlay. Produces ADR-0001.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-002, ST-003, ST-004
- ⛓️ **Blocked By:** None

**Acceptance Criteria:**
- [ ] GIVEN hooks, UIA polling and whisper.net are active, WHEN a user types for 5 minutes, THEN added input latency < 5 ms and no hook is unhooked by the OS
- [ ] GIVEN an RDP window is focused, WHEN FlaUI queries the focused element, THEN it reports an opaque subtree
- [ ] GIVEN the overlay uses `WDA_EXCLUDEFROMCAPTURE`, WHEN the service captures, THEN the overlay is absent from the frame
- [ ] GIVEN a 4K frame downscaled to ≤ 1600 px JPEG, THEN size < 400 KB with legible 9-pt UI text
- [ ] ADR at `/docs/adr/0001-client-stack.md` records decision and Python fallback assessment

**Agent brief**
- Read: `00-Build-Agent-Guide.md` §2, §5
- Write: `/spike/**` (disposable), `/docs/adr/0001-client-stack.md`
- Verify: manual latency log attached to PR; screenshot proving overlay exclusion
---

---
### **ST-002 · Bootstrap monorepo, CI and code-quality gates**
- **Epic/Feature:** INFRA
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
Repository layout per Guide §2, with analyzers, formatting, unit tests, Windows client build and backend container build on every PR.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-005, ST-007, ST-013
- ⛓️ **Blocked By:** ST-001

**Acceptance Criteria:**
- [ ] GIVEN a PR, WHEN CI runs, THEN lint, analyzers, unit tests, Windows build and backend container build must pass before merge
- [ ] `main` branch-protected with required checks; conventional-commit lint enabled
- [ ] README documents local setup in ≤ 10 steps for client and backend

**Agent brief**
- Read: Guide §2, §5
- Write: `/.github/workflows/*`, `/client/*.sln`, `/backend/*`, `/web/*` skeletons, `/README.md`, `.editorconfig`
- Verify: green CI on a PR touching each of client, backend, web
---

---
### **ST-003 · Define shared session schema and event contracts**
- **Epic/Feature:** INFRA
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
`session.v1.json`: events, frames (`redaction_pending`, `ocr_text`, `masked_regions`, `sensitive_context`), transcript segments, draft note; generated C# and TS types in CI.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-006, ST-020, ST-060, ST-074
- ⛓️ **Blocked By:** ST-001

**Acceptance Criteria:**
- [ ] Schema validates fixtures; codegen produces C# (`ScreenTail.Shared`) and TS (`/web/src/generated`) in CI
- [ ] Every event/frame carries monotonic `ts_ms`; frames carry `redaction_pending`, `redacted_at`, `masked_regions[]`, `sensitive_context`
- [ ] Transcript segments carry `speaker` (`tech`; `end_user` reserved); draft steps carry `confidence`, `frame_refs[]`

**Agent brief**
- Read: Guide §3 (INV-1, INV-2), Spec §5 S3 (fields the Review needs)
- Write: `/shared/schema/session.v1.json`, codegen config, `/client/ScreenTail.Shared/Generated`, `/web/src/generated`
- Verify: `npm run codegen && dotnet build`; schema tests in `ScreenTail.Tests/Schema`
---

---
### **ST-004 · Client process architecture: capture service + UI + authenticated IPC**
- **Epic/Feature:** INFRA
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
Per-user background capture service and separate WPF UI process connected by a named pipe with per-session tokens; UI can crash and restart without losing a recording.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-012, ST-020, ST-070
- ⛓️ **Blocked By:** ST-001

**Acceptance Criteria:**
- [ ] GIVEN a recording session, WHEN the UI process is killed, THEN capture continues and the restarted UI reattaches within 2 s
- [ ] GIVEN an unsigned process connects, WHEN it sends `start`, THEN it is rejected and audit-logged
- [ ] IPC contract versioned and documented; round-trip < 10 ms
- [ ] Service starts at login; UI on demand or at login per settings

**Agent brief**
- Read: Guide §5 (IPC), INV-4
- Write: `/client/ScreenTail.Service/Host/*`, `/client/ScreenTail.Shared/Ipc/*`, `/client/ScreenTail.UI/Ipc/*`, `/docs/ipc-contract.md`
- Verify: `ScreenTail.Tests/Ipc/*` (auth rejection, reattach), manual kill/restart demo
---

---
### **ST-005 · Encrypted local store with two-stage frame lifecycle**
- **Epic/Feature:** INFRA
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
SQLite + SQLCipher keyed via DPAPI (user scope). Frames written encrypted with `redaction_pending: true`; the read API used by Review, bundle builder and export returns only redacted frames; cleanup deletes frames still pending after finalize grace.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-020, ST-041, ST-044, ST-045, ST-064
- ⛓️ **Blocked By:** ST-002

**Acceptance Criteria:**
- [ ] GIVEN a saved session, WHEN the DB is opened outside the app, THEN contents are ciphertext; a different Windows user cannot decrypt a copied DB
- [ ] GIVEN a frame with `redaction_pending: true`, WHEN Review/bundle/export query frames, THEN it is not returned (INV-1)
- [ ] GIVEN 3 frames still pending after grace at finalize, WHEN cleanup runs, THEN they are deleted and an audit row records the count
- [ ] ≥ 10 frame writes/s without blocking capture; 20-minute session < 40 MB

**Agent brief**
- Read: INV-1, INV-12; Spec §5 S3 banners ("Frames purged")
- Write: `/client/ScreenTail.Service/Store/*`, `/client/ScreenTail.Service/Migrations/*.sql`
- Verify: `ScreenTail.Tests/Store/*` including `PendingFramesNeverReadable` and `DifferentUserCannotDecrypt`
---

---
### **ST-006 · Author hand-crafted fixture bundles (UI and prompt v1)**
- **Epic/Feature:** INFRA
- **Priority:** Urgent
- **Estimate:** 2

**Description:**
Five schema-valid bundles assembled from staged VM screenshots, written transcripts and click lists, plus 20 frames with seeded secrets. Unblocks Review UI and prompt work before the capture engine exists.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-042, ST-061, ST-074
- ⛓️ **Blocked By:** ST-003

**Acceptance Criteria:**
- [ ] 5 bundles validate and load in Review with no backend
- [ ] Fixtures include RDP and ScreenConnect chrome, one error dialog, one login prompt
- [ ] 20 labeled secret frames in `/research/fixtures/redaction-seed/`

**Agent brief**
- Read: ST-003 schema; Spec §5 S3
- Write: `/research/fixtures/handcrafted/*`, `/research/fixtures/redaction-seed/*`, `/research/fixtures/README.md`
- Verify: `pytest research/tests/test_fixtures.py`
---

---
### **ST-007 · Provision cloud infrastructure and environments (IaC)**
- **Epic/Feature:** INFRA
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
Terraform for staging/production: API hosting, managed Postgres, KMS, secrets manager, TLS, release-feed bucket. No object storage for frames (INV-7).

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-008, ST-113
- ⛓️ **Blocked By:** ST-002

**Acceptance Criteria:**
- [ ] `terraform apply` on a clean account yields a staging health endpoint over HTTPS
- [ ] Production deploy requires manual approval in CI
- [ ] Cost estimate documented; target < $150/month at pilot scale

**Agent brief**
- Read: INV-7
- Write: `/infra/**`, `/.github/workflows/deploy.yml`, `/docs/infra.md`
- Verify: plan/apply logs in PR; health check URL
---

---
### **ST-008 · Backend API scaffold with auth and Postgres**
- **Epic/Feature:** INFRA
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
ASP.NET Core minimal API; Postgres via EF Core for tenants/users/devices/integrations/policies/metrics; JWT tenant and device tokens; never persists raw captures.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-009, ST-010, ST-047, ST-063, ST-090, ST-098
- ⛓️ **Blocked By:** ST-002, ST-007

**Acceptance Criteria:**
- [ ] GIVEN a valid device token, WHEN `GET /v1/me`, THEN tenant/user/device info; invalid → 401
- [ ] Migrations for tenants, users, devices, integrations, policies, session_metrics
- [ ] Test asserts DB/storage size unchanged after a summarization request (INV-7)

**Agent brief**
- Read: INV-7, INV-10
- Write: `/backend/src/**`, `/backend/migrations/**`, `/backend/tests/**`
- Verify: `dotnet test backend`; OpenAPI served at `/swagger`
---

---
### **ST-009 · Encrypted integration-credential vault**
- **Epic/Feature:** INFRA
- **Priority:** High
- **Estimate:** 3

**Description:**
Envelope encryption (KMS key + per-record data key) for PSA/doc credentials; readable only by provider workers; masked in API responses.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-091, ST-095
- ⛓️ **Blocked By:** ST-008

**Acceptance Criteria:**
- [ ] Stored credential rows are ciphertext
- [ ] `GET /v1/integrations` returns last-4 masked values only
- [ ] Rotation procedure documented and exercised once

**Agent brief**
- Read: ST-008 data model
- Write: `/backend/src/Vault/*`, `/docs/security/key-rotation.md`
- Verify: `backend/tests/Vault/*`
---

---
### **ST-010 · Tenant signup, technician invites, device activation, seats, offboarding**
- **Epic/Feature:** INFRA
- **Priority:** High
- **Estimate:** 5

**Description:**
Owner signup (email/password or Microsoft SSO), invites, device activation by code, seat counting, 7-day offline licence grace, tenant offboarding with full deletion.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-047, ST-083, ST-099, ST-120
- ⛓️ **Blocked By:** ST-008

**Acceptance Criteria:**
- [ ] Invite email with 72 h code; activation binds device to tenant
- [ ] 6th device on a 5-seat tenant is refused: "Seat limit reached — contact your admin"
- [ ] Backend unreachable 7 days → local-only drafts; capture never stops mid-session
- [ ] Offboarding deletes all tenant rows, credentials and metrics within 24 h and emails a receipt

**Agent brief**
- Read: Spec §5 S9 (web copy), Spec §5 S8 step 2
- Write: `/backend/src/Tenancy/*`, `/backend/src/Licensing/*`, client licence check in `/client/ScreenTail.Service/Licensing/*`
- Verify: `backend/tests/Tenancy/*`, client test `OfflineGraceKeepsCapturing`
---

---
### **ST-011 · Structured logging, crash reporting, PII scrubber**
- **Epic/Feature:** INFRA
- **Priority:** High
- **Estimate:** 2

**Description:**
Structured logs across service, UI, backend with a scrubber removing paths, window titles, transcript/OCR/note text; opt-in crash reports (stack traces only).

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-112
- ⛓️ **Blocked By:** ST-008

**Acceptance Criteria:**
- [ ] Log lines with window title or transcript show `[redacted]` (INV-10)
- [ ] Crash → stack-trace-only report queued if opted in
- [ ] Log level configurable; default INFO

**Agent brief**
- Read: INV-10
- Write: `/client/ScreenTail.Shared/Logging/*`, `/backend/src/Logging/*`
- Verify: `ScreenTail.Tests/Logging/ScrubberTests`
---

---
### **ST-012 · Threat model and client hardening**
- **Epic/Feature:** INFRA
- **Priority:** High
- **Estimate:** 3

**Description:**
STRIDE threat model with mitigations: signed binaries, authenticated IPC, no debug endpoints in release, service-binary integrity check, hash-chained audit log.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-114
- ⛓️ **Blocked By:** ST-004

**Acceptance Criteria:**
- [ ] `/docs/security/threat-model.md` with STRIDE table mapped to tickets
- [ ] Release build: no listening TCP ports, no debug flags
- [ ] Modified service binary → UI refuses to connect and alerts

**Agent brief**
- Read: Guide §3 all invariants
- Write: `/docs/security/threat-model.md`, integrity check in `/client/ScreenTail.UI/Ipc/*`
- Verify: `netstat` proof in PR; `IntegrityCheckTests`
---

---
### **ST-013 · Windows VM end-to-end test harness in CI**
- **Epic/Feature:** INFRA
- **Priority:** High
- **Estimate:** 3

**Description:**
Windows 11 VM job installing the built client, replaying golden sessions through the real service and asserting bundle contents, redaction results and performance budgets. Nightly and on release branches.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-031, ST-115
- ⛓️ **Blocked By:** ST-002, ST-030

**Acceptance Criteria:**
- [ ] Golden replay produces a bundle within ±5% event count and exact redaction count
- [ ] Reports CPU/RAM/disk per run
- [ ] Nightly failure notifies the team channel

**Agent brief**
- Read: ST-030 recorder format
- Write: `/tests/e2e/**`, `/.github/workflows/nightly-e2e.yml`
- Verify: nightly run link in PR
---

## Phase 1 — Design & UX (Epic: DESIGN) — parallel with Phase 2

---
### **ST-014 · Low-fidelity wireframes for all v1 screens**
- **Epic/Feature:** DESIGN
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
Wireframes for S1–S8 from Spec §5 with annotated flows (happy path, sensitive moment, bad draft, missed start, discard); reviewed with two technicians.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-015, ST-016
- ⛓️ **Blocked By:** None

**Acceptance Criteria:**
- [ ] Wireframes for S1–S8 exported to `/docs/ux/wireframes/`
- [ ] Two technicians complete "accept this note" on the Review wireframe unprompted
- [ ] Open questions in Spec (HUD visibility during screen-share, default note type) answered and written into Spec

**Agent brief**
- Read: Spec §1, §4, §5
- Write: `/docs/ux/wireframes/*`, Spec amendments via PR
- Verify: reviewer notes attached
---

---
### **ST-015 · Hi-fi design and clickable prototype for the Review screen**
- **Epic/Feature:** DESIGN
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
Pixel-level Review (Spec §5 S3) in both themes; clickable prototype covering ≥ 8 states.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-017, ST-074
- ⛓️ **Blocked By:** ST-014

**Acceptance Criteria:**
- [ ] States: low-confidence step, suppressed marker, partial-capture banner, purged-frames banner, offline, draft failed, publish partial failure, published
- [ ] Both themes pass AA contrast
- [ ] Shortcut map matches Spec §5 S3

**Agent brief**
- Read: Spec §2, §3, §5 S3
- Write: `/docs/ux/review-hifi/*`, prototype link in README
- Verify: contrast report attached
---

---
### **ST-016 · Design system tokens and WPF component library**
- **Epic/Feature:** DESIGN
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
Implement Spec §2 tokens as `/shared/design/tokens.json` consumed by WPF resource dictionaries and web CSS variables; base components from Spec §3 with a gallery window.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-070, ST-099
- ⛓️ **Blocked By:** ST-014

**Acceptance Criteria:**
- [ ] Single token source; theme switch requires no per-component changes; high-contrast mapping present
- [ ] Token test asserts every text/background pair ≥ 4.5:1
- [ ] Gallery renders every Spec §3 component in both themes with focus rings

**Agent brief**
- Read: Spec §2, §3, §7
- Write: `/shared/design/tokens.json`, `/client/ScreenTail.UI/Theme/*`, `/client/ScreenTail.UI/Controls/*`, `/client/ScreenTail.UI/Gallery/*`
- Verify: `ScreenTail.Tests/Theme/ContrastTests`; gallery screenshots both themes
---

---
### **ST-017 · Usability test round 1 (Review flow, 5 technicians)**
- **Epic/Feature:** DESIGN
- **Priority:** High
- **Estimate:** 3

**Description:**
Moderated sessions on the prototype measuring time-to-publish, errors and confusion against Spec §8 metrics.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-078
- ⛓️ **Blocked By:** ST-015

**Acceptance Criteria:**
- [ ] ≥ 4/5 publish a corrected note in < 60 s
- [ ] Findings filed as Spec changes or tickets with owners
- [ ] HUD visibility policy decided and written to Spec §5 S2

**Agent brief**
- Read: Spec §8
- Write: `/docs/ux/usability-round-1.md`, Spec amendments
- Verify: report merged
---

## Phase 2 — Capture Engine (Epic: CAPTURE)

---
### **ST-020 · Session state machine**
- **Epic/Feature:** CAPTURE
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
`idle → recording[/suppressed] → paused → finalizing → draft_ready|draft_failed`; coordinates hooks/screenshots/STT; persists transitions; streams state over IPC; recovers orphans; enforces 20 s redaction grace at finalize.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-021, ST-022, ST-024, ST-027, ST-029, ST-066, ST-071
- ⛓️ **Blocked By:** ST-003, ST-004, ST-005

**Acceptance Criteria:**
- [ ] `start()` → session row + `recording` within 200 ms with IPC event
- [ ] `pause()` and `suppressed` write no events/frames/transcript (INV-6)
- [ ] `stop()` → `finalizing`, waits ≤ 20 s for pending redactions, deletes stragglers, then `draft_ready`/`draft_failed(reason)`
- [ ] Crash mid-session → recovered as `finalizing` with `partial_capture: true`

**Agent brief**
- Read: INV-4, INV-6; Spec §5 S1/S2 state names
- Write: `/client/ScreenTail.Service/Session/*`
- Verify: `ScreenTail.Tests/Session/StateMachineTests` (all transitions, crash recovery)
---

---
### **ST-021 · Windows permission and capability checks**
- **Epic/Feature:** CAPTURE
- **Priority:** Urgent
- **Estimate:** 2

**Description:**
Verify mic privacy permission, capture ability, hook installation, elevated-window blindness; report with fix hints via IPC.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-072, ST-083
- ⛓️ **Blocked By:** ST-020

**Acceptance Criteria:**
- [ ] Mic privacy off → HUD "Microphone is blocked by Windows privacy settings" with deep link
- [ ] Elevated window focused → HUD "Elevated window — screen not captured"; interval audit-logged
- [ ] Capability report exposed via IPC

**Agent brief**
- Read: Spec §5 S2, §6 capability copy
- Write: `/client/ScreenTail.Service/Capabilities/*`
- Verify: `CapabilityTests`; manual screenshot of HUD messages
---

---
### **ST-022 · Foreground-window detection service**
- **Epic/Feature:** CAPTURE
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
`EVENT_SYSTEM_FOREGROUND` (fallback polling); resolve PID/process/title/class; read active browser tab title via UIA.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-023, ST-040, ST-043, ST-077
- ⛓️ **Blocked By:** ST-020

**Acceptance Criteria:**
- [ ] Focus change → event with correct process and title within 100 ms
- [ ] Chrome/Edge focused → active tab title included
- [ ] Idle CPU < 0.5%; elevated windows degrade gracefully

**Agent brief**
- Read: ST-023 needs (title patterns)
- Write: `/client/ScreenTail.Service/Detection/ForegroundWatcher.cs`
- Verify: `ForegroundWatcherTests` with synthetic windows
---

---
### **ST-023 · Remote-tool registry, browser patterns and capture-scope policy**
- **Epic/Feature:** CAPTURE
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
JSON registry of remote-tool processes and browser title/URL patterns; auto-start on focus, stop after grace; capture scope default = remote-tool windows + admin-tool allowlist (INV-5); records client versions.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-025, ST-080
- ⛓️ **Blocked By:** ST-022

**Acceptance Criteria:**
- [ ] ScreenConnect window or AVD web tab focused → session starts within 1 s
- [ ] Default scope: switching to Outlook logs clicks but captures no frames; HUD "Not capturing — Outlook"
- [ ] PowerShell (allowlisted) → frames captured
- [ ] Grace 90 s default; registry/allowlist editable and policy-syncable; fixtures for 5 tools + 3 browser patterns; client version logged

**Agent brief**
- Read: INV-5; Spec §5 S2 scope pill, S5 scope copy
- Write: `/client/ScreenTail.Service/Detection/Registry/*`, `/client/ScreenTail.Service/Detection/ScopePolicy.cs`, `/shared/registry/remote-tools.json`
- Verify: `RegistryTests`, `ScopePolicyTests`
---

---
### **ST-024 · Low-level mouse/keyboard hooks with event buffer**
- **Epic/Feature:** CAPTURE
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
`WH_MOUSE_LL`/`WH_KEYBOARD_LL` on a dedicated thread; lock-free buffer; clicks record position/button/hwnd; keys record categories only (INV-2).

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-025, ST-060
- ⛓️ **Blocked By:** ST-020

**Acceptance Criteria:**
- [ ] Click → `{x,y,button,hwnd,ts_ms}` buffered within 2 ms; callback returns < 1 ms
- [ ] Typing a password stores only `{type:"typing_burst", char_count, ts_ms}`
- [ ] Hooks re-install after sleep/resume and lock/unlock

**Agent brief**
- Read: INV-2
- Write: `/client/ScreenTail.Service/Input/*`
- Verify: `HookTests` (no character capture assertion), latency log
---

---
### **ST-025 · Screenshot-on-click with debounce and downscale**
- **Epic/Feature:** CAPTURE
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
On in-scope click: capture active window at native res with per-monitor DPI, debounce 400 ms, cursor marker, downscale ≤ 1600 px JPEG, write encrypted with `redaction_pending: true`.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-026, ST-030, ST-041
- ⛓️ **Blocked By:** ST-023, ST-024

**Acceptance Criteria:**
- [ ] In-scope click → staged frame with cursor marker, correct DPI, `redaction_pending: true`
- [ ] Out-of-scope click → no frame
- [ ] 5 clicks in 400 ms → one frame; 4K capture-to-staged-write < 120 ms; HUD pixels absent

**Agent brief**
- Read: INV-1, INV-5
- Write: `/client/ScreenTail.Service/Capture/ScreenshotCapturer.cs`
- Verify: `ScreenshotTests`; frame sample in PR
---

---
### **ST-026 · Scene-change frame sampling**
- **Epic/Feature:** CAPTURE
- **Priority:** High
- **Estimate:** 3

**Description:**
1 fps sampling of the in-scope window; keep frames when perceptual-hash distance exceeds threshold.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-031, ST-060
- ⛓️ **Blocked By:** ST-025

**Acceptance Criteria:**
- [ ] Static screen 60 s → zero frames
- [ ] Error dialog appears → frame within 2 s
- [ ] ≤ 60 scene frames on a 20-minute golden session

**Agent brief**
- Read: ST-025
- Write: `/client/ScreenTail.Service/Capture/SceneSampler.cs`
- Verify: `SceneSamplerTests` with recorded frame sequences
---

---
### **ST-027 · Local speech-to-text pipeline (whisper.net)**
- **Epic/Feature:** CAPTURE
- **Priority:** Urgent
- **Estimate:** 8

**Description:**
NAudio mic capture, VAD, local transcription (base/small CPU, larger GPU), timestamped `speaker: "tech"` segments (INV-9); lazy resumable model download.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-028, ST-030, ST-080, ST-123
- ⛓️ **Blocked By:** ST-020

**Acceptance Criteria:**
- [ ] 10-minute narrated test → WER ≤ 15% default model
- [ ] Mic muted/missing → capture continues; HUD "No microphone"
- [ ] Model absent → progress shown, app responsive
- [ ] Lag ≤ 5 s on 4-core laptop

**Agent brief**
- Read: INV-9; Spec §5 S2 mic states, S5 model-size copy
- Write: `/client/ScreenTail.Service/Speech/*`
- Verify: `SpeechTests` against `/research/fixtures/audio/*`; WER script `research/eval/wer.py`
---

---
### **ST-028 · Transcript-to-timeline alignment**
- **Epic/Feature:** CAPTURE
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
Merge transcript, click, frame and marker events; attach segments to nearest preceding frame within ±8 s else standalone narration.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-030, ST-060, ST-076
- ⛓️ **Blocked By:** ST-027

**Acceptance Criteria:**
- [ ] Click at 12.0 s + segment 10.5–14.2 s → linked
- [ ] No frame within ±8 s → `narration` event
- [ ] Output validates against schema

**Agent brief**
- Read: ST-003
- Write: `/client/ScreenTail.Service/Timeline/Aligner.cs`
- Verify: `AlignerTests`
---

---
### **ST-029 · Global hotkeys (start / pause / stop / discard / mark moment)**
- **Epic/Feature:** CAPTURE
- **Priority:** High
- **Estimate:** 2

**Description:**
Configurable chords per Spec §5 S1 defaults; "mark moment" forces a frame and marker.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-072
- ⛓️ **Blocked By:** ST-020

**Acceptance Criteria:**
- [ ] Pause chord → `paused` and HUD update within 200 ms
- [ ] Conflict → Settings warning with suggested alternative
- [ ] Mark moment stores frame + `marker` regardless of debounce

**Agent brief**
- Read: Spec §5 S1 chords
- Write: `/client/ScreenTail.Service/Input/Hotkeys.cs`
- Verify: `HotkeyTests`
---

---
### **ST-030 · Dev-mode session recorder and real golden dataset**
- **Epic/Feature:** CAPTURE
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
Developer flag exporting full bundles from scripted VM scenarios against real RDP and ScreenConnect; produces 10 golden sessions and ≥ 50 labeled redaction frames.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-013, ST-042, ST-062
- ⛓️ **Blocked By:** ST-025, ST-027, ST-028

**Acceptance Criteria:**
- [ ] 10 golden bundles across RDP and ScreenConnect, 5 fix types
- [ ] ≥ 50 corpus frames with labeled secret boxes
- [ ] Recorder compiled out of release builds

**Agent brief**
- Read: ST-006 layout
- Write: `/client/ScreenTail.Service/Dev/Recorder.cs` (DEBUG only), `/research/fixtures/golden/*`, `/research/fixtures/redaction-corpus/*`
- Verify: `pytest research/tests/test_golden.py`
---

---
### **ST-031 · Performance budget and idle behavior**
- **Epic/Feature:** CAPTURE
- **Priority:** High
- **Estimate:** 3

**Description:**
Budgets: idle CPU < 1%, recording CPU < 15% (4-core), RAM < 600 MB with STT, disk < 40 MB per 20 min; self-throttle sampling/OCR first; live figures in "What's being captured".

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116
- ⛓️ **Blocked By:** ST-013, ST-026, ST-027, ST-041

**Acceptance Criteria:**
- [ ] 30-minute golden replay on reference laptop holds all budgets
- [ ] CPU over budget 10 s → sampling and OCR degrade before hooks/STT
- [ ] Nightly harness asserts budgets

**Agent brief**
- Read: Spec §5 S1 diagnostics panel
- Write: `/client/ScreenTail.Service/Perf/*`, e2e budget assertions
- Verify: nightly run report
---

## Phase 3 — Privacy & Redaction (Epic: PRIVACY)

---
### **ST-040 · Password-field suppression via UI Automation**
- **Epic/Feature:** PRIVACY
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
On UIA focus change read `IsPassword` (fallback `EM_GETPASSWORDCHAR`); enter `suppressed`, drop frames/typing, notify HUD, audit interval (INV-6).

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-045, ST-072, ST-115
- ⛓️ **Blocked By:** ST-022

**Acceptance Criteria:**
- [ ] Local password field focused → zero frames and typing events stored
- [ ] Focus leaves → capture resumes on next click
- [ ] Intervals audit-logged

**Agent brief**
- Read: INV-6; Spec §5 S2 "Paused: sensitive field"
- Write: `/client/ScreenTail.Service/Privacy/PasswordFieldGuard.cs`
- Verify: `PasswordFieldGuardTests` against a WPF test app with a PasswordBox
---

---
### **ST-041 · OCR and redaction worker (staged → redacted frames)**
- **Epic/Feature:** PRIVACY
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
Background worker pulls `redaction_pending` frames, runs OCR (Tesseract .NET) for text + boxes, applies login-screen heuristic, hands to pattern engine, flips flag; bounded concurrency; backlog depth exposed via IPC.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-031, ST-042, ST-060
- ⛓️ **Blocked By:** ST-005, ST-025

**Acceptance Criteria:**
- [ ] 1080p settings dialog → ≥ 90% labels recognized; frame becomes readable by Review
- [ ] Remote-canvas login prompt → `sensitive_context` and 10 s suppression
- [ ] 200 staged frames → median < 700 ms/frame; backlog depth reported every second
- [ ] Per-session isolation test passes

**Agent brief**
- Read: INV-1; Spec §5 S1 "redaction backlog" field
- Write: `/client/ScreenTail.Service/Privacy/RedactionWorker.cs`, `/client/ScreenTail.Service/Privacy/Ocr/*`
- Verify: `RedactionWorkerTests`, isolation test
---

---
### **ST-042 · Pattern-based redaction engine (regex + OCR mask + transcript scrub)**
- **Epic/Feature:** PRIVACY
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
Pattern library (SSN, Luhn cards, API keys, password pairs, optional emails) masks matching boxes in stored frames and scrubs OCR/transcript text; tenant-custom regex.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-060, ST-081, ST-115
- ⛓️ **Blocked By:** ST-006, ST-030, ST-041

**Acceptance Criteria:**
- [ ] Luhn-valid 16-digit → masked; OCR text `[CARD]`
- [ ] Transcript "the password is Winter2026" → `[REDACTED]`
- [ ] Real corpus: ≥ 98% recall, ≤ 2% false-positive masks
- [ ] Custom regex per tenant, policy-syncable

**Agent brief**
- Read: INV-1; Spec §5 S6 pattern toggles
- Write: `/client/ScreenTail.Service/Privacy/Patterns/*`
- Verify: `pytest research/eval/redaction_recall.py` gate in CI; `PatternEngineTests`
---

---
### **ST-043 · App/window exclusion list**
- **Epic/Feature:** PRIVACY
- **Priority:** High
- **Estimate:** 2

**Description:**
Processes/title substrings/URL fragments that trigger `suppressed`; defaults for password managers and banking; policy-syncable.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-081, ST-115
- ⛓️ **Blocked By:** ST-022

**Acceptance Criteria:**
- [ ] `1Password.exe` focused → no frames; HUD "Paused: excluded app"
- [ ] Tab title containing "bank" → suppression
- [ ] ≥ 10 password managers in defaults

**Agent brief**
- Read: Spec §5 S6 "Excluded apps"
- Write: `/client/ScreenTail.Service/Privacy/ExclusionList.cs`, `/shared/registry/exclusions-default.json`
- Verify: `ExclusionListTests`
---

---
### **ST-044 · Retention purge job**
- **Epic/Feature:** PRIVACY
- **Priority:** High
- **Estimate:** 2

**Description:**
Delete raw frames/OCR/transcripts/outbox older than retention (default 7 d); keep note text and audit; "Delete everything now" also removes tokens (INV-12).

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-081
- ⛓️ **Blocked By:** ST-005

**Acceptance Criteria:**
- [ ] Session older than retention → raw rows deleted, DB vacuumed
- [ ] Retention 1 day → after 25 h raw gone, note remains
- [ ] Delete everything wipes data and tokens

**Agent brief**
- Read: INV-12; Spec §5 S6 retention copy
- Write: `/client/ScreenTail.Service/Store/RetentionJob.cs`
- Verify: `RetentionJobTests` with clock injection
---

---
### **ST-045 · Capture audit log**
- **Epic/Feature:** PRIVACY
- **Priority:** High
- **Estimate:** 3

**Description:**
Append-only hash-chained log of captured/suppressed/redacted/purged/sent/published events; export JSON/CSV without content.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-079, ST-114
- ⛓️ **Blocked By:** ST-005, ST-040

**Acceptance Criteria:**
- [ ] Completed session log lists frames captured, purged-unredacted, suppressed intervals, redactions by type, bytes sent, destinations
- [ ] Export contains no OCR/transcript
- [ ] Hash chain verifies

**Agent brief**
- Read: INV-10; Spec §5 S4 export
- Write: `/client/ScreenTail.Service/Audit/*`
- Verify: `AuditLogTests` (chain tamper detection)
---

---
### **ST-046 · Local-only mode switch and data-egress guard**
- **Epic/Feature:** PRIVACY
- **Priority:** High
- **Estimate:** 3

**Description:**
Global/admin-enforceable setting routing summarization on-device and blocking all egress except user-initiated publish, via HTTP allowlist (INV-8).

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-065, ST-081, ST-114
- ⛓️ **Blocked By:** ST-005

**Acceptance Criteria:**
- [ ] Local-only on → zero requests to backend/LLM at finalize (network capture)
- [ ] Publish → only PSA/Hudu hosts contacted
- [ ] Shown in tray tooltip and HUD; not disable-able locally when policy-enforced

**Agent brief**
- Read: INV-8, INV-11
- Write: `/client/ScreenTail.Service/Net/EgressGuard.cs`
- Verify: `EgressGuardTests` with fake HTTP handler
---

---
### **ST-047 · Tenant policy sync (admin-enforced settings)**
- **Epic/Feature:** PRIVACY
- **Priority:** High
- **Estimate:** 3

**Description:**
Backend policy (retention, Local-only enforce, exclusions, patterns, scope, telemetry) fetched at start and hourly; enforced fields read-only with "Set by your admin" (INV-11).

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-081, ST-099, ST-120
- ⛓️ **Blocked By:** ST-008, ST-010

**Acceptance Criteria:**
- [ ] Admin retention 3 d → client retention 3 d, field locked
- [ ] Backend unreachable → last synced policy applied
- [ ] Policy version in every session's audit log

**Agent brief**
- Read: INV-11; Spec §3 Toggle locked variant, §5 S9 Policy page
- Write: `/backend/src/Policy/*`, `/client/ScreenTail.Service/Policy/*`
- Verify: `PolicySyncTests` both sides
---

## Phase 4 — Intelligence Layer (Epic: INTEL)

---
### **ST-060 · Session bundle builder**
- **Epic/Feature:** INTEL
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
From redacted frames only, select ≤ 25 informative frames; assemble timeline, OCR, transcript, metadata, style hints; set `ocr_partial` and `frames_purged_unredacted`.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-063, ST-064, ST-065
- ⛓️ **Blocked By:** ST-003, ST-024, ST-026, ST-028, ST-041, ST-042

**Acceptance Criteria:**
- [ ] 200 redacted frames → ≤ 25 selected; payload < 4 MB
- [ ] Any `redaction_pending`, `suppressed` or `sensitive_context` frame never included (adversarial store test)
- [ ] Token estimate logged per bundle

**Agent brief**
- Read: INV-1
- Write: `/client/ScreenTail.Service/Intel/BundleBuilder.cs`
- Verify: `BundleBuilderTests` incl. `NeverIncludesPendingFrames`
---

---
### **ST-061 · Note-generation prompt and output schema (versioned)**
- **Epic/Feature:** INTEL
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
System prompt and strict JSON output (`problem`, `steps[]{text,confidence,frame_refs}`, `result`, `follow_ups`, `suggested_title`, `suggested_time_minutes`, `kb_candidate`); versioned files; first on fixtures then golden set.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-062, ST-063, ST-065
- ⛓️ **Blocked By:** ST-003, ST-006

**Acceptance Criteria:**
- [ ] 100% fixture outputs validate
- [ ] Screen-only steps → `confidence: low`
- [ ] Adversarial unrelated frame → no invented step

**Agent brief**
- Read: Spec §5 S3 (how fields render), §4 copy voice for note text
- Write: `/research/prompts/note_v1.md`, `/research/prompts/schema.json`
- Verify: `pytest research/tests/test_prompt_schema.py`
---

---
### **ST-062 · Draft-quality evaluation harness**
- **Epic/Feature:** INTEL
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
Human rubric (accuracy, completeness, no hallucination, tone) plus automated edit-distance proxy against reference notes; runs on prompt/model changes; regression gate.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-063 (release gate), ST-065
- ⛓️ **Blocked By:** ST-030, ST-061

**Acceptance Criteria:**
- [ ] Golden set has reference notes and rubric scores committed
- [ ] Prompt change → CI reports edit distance and hallucination count; > 10% regression fails
- [ ] Cloud baseline recorded (target mean edit ratio < 25%)

**Agent brief**
- Read: Spec §8 metrics
- Write: `/research/eval/*`, `/research/fixtures/golden/references/*`, CI job
- Verify: harness run output in PR
---

---
### **ST-063 · Summarization endpoint with provider abstraction, fallback and cost cap**
- **Epic/Feature:** INTEL
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
`POST /v1/sessions/summarize` behind a provider interface (Gemini Flash default; OpenAI/Anthropic swappable) with fallback provider, schema validation + one repair retry, per-tenant daily cost cap; frames in memory only (INV-7).

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-067
- ⛓️ **Blocked By:** ST-008, ST-060, ST-061

**Acceptance Criteria:**
- [ ] Valid bundle → schema-valid draft within 30 s p95
- [ ] Primary 5xx/timeout → fallback used and logged
- [ ] Cap exceeded → `cost_cap_reached`; client produces local text-only draft
- [ ] Cost stored per request; golden average ≤ $0.10; memory profiler shows frames released

**Agent brief**
- Read: INV-7; Spec §6 "Cost cap reached" toast
- Write: `/backend/src/Summarize/*`, `/backend/src/Providers/Llm/*`
- Verify: `backend/tests/Summarize/*`, storage-size test, cost report
---

---
### **ST-064 · Offline queue for summarization and publish (outbox)**
- **Epic/Feature:** INTEL
- **Priority:** High
- **Estimate:** 3

**Description:**
Local outbox with idempotency keys; retry with backoff; "Draft pending — offline" state.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-073
- ⛓️ **Blocked By:** ST-005, ST-060

**Acceptance Criteria:**
- [ ] Network down at finalize → completes on reconnect; user notified
- [ ] Publish timeout after server acceptance → no duplicate note
- [ ] Outbox payloads respect retention

**Agent brief**
- Read: Spec §5 S2 offline pill, §6 offline toast
- Write: `/client/ScreenTail.Service/Outbox/*`
- Verify: `OutboxTests` with fake network
---

---
### **ST-065 · On-device summarization fallback (text-first Local-only)**
- **Epic/Feature:** INTEL
- **Priority:** High
- **Estimate:** 8

**Description:**
OCR text + transcript + click log → small local text model (Llama 3.2 3B / Phi-3.5-mini via llama.cpp Q4); optional vision when GPU present; flagged lower confidence.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-046, ST-060, ST-061, ST-062

**Acceptance Criteria:**
- [ ] 16 GB laptop, no GPU → schema-valid draft < 90 s
- [ ] Harness reports edit ratio (target < 40%), no hallucinated steps
- [ ] Model load failure → structured transcript + click-list draft with clear message
- [ ] Model download opt-in with size; stored under encrypted app data

**Agent brief**
- Read: INV-8; Spec §5 S3 "Local draft" banner
- Write: `/client/ScreenTail.Service/Intel/LocalSummarizer/*`, `/research/prompts/note_local_v1.md`
- Verify: harness run on local model; `LocalSummarizerTests`
---

---
### **ST-066 · Suggested time-entry calculation**
- **Epic/Feature:** INTEL
- **Priority:** High
- **Estimate:** 2

**Description:**
Active duration minus pauses with tenant rounding (6/15) and minimum increment.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-078
- ⛓️ **Blocked By:** ST-020

**Acceptance Criteria:**
- [ ] 23 active min, 15-min rounding → 30
- [ ] 6 paused min excluded
- [ ] Rounding/minimum configurable per tenant

**Agent brief**
- Read: Spec §5 S3 time entry field
- Write: `/client/ScreenTail.Service/Intel/TimeEntryCalculator.cs`
- Verify: `TimeEntryCalculatorTests`
---

---
### **ST-067 · Style-feedback store (learn from edits)**
- **Epic/Feature:** INTEL
- **Priority:** Medium
- **Estimate:** 3

**Description:**
Diff draft vs final on publish; store structural style signals only; inject top signals as prompt hints.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-063, ST-074

**Acceptance Criteria:**
- [ ] Consistent past-tense rewrites over 5 sessions → tense hint on 6th
- [ ] No client names, ticket content or PII stored
- [ ] Edit rate per tenant tracked

**Agent brief**
- Read: INV-10
- Write: `/backend/src/Style/*`, client diff in `/client/ScreenTail.Service/Intel/StyleDiff.cs`
- Verify: `StyleDiffTests`, PII-absence test
---

## Phase 5 — Client UI (Epic: UI)

---
### **ST-070 · Client app shell (window manager, navigation, MVVM state, IPC client, theming)**
- **Epic/Feature:** UI
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
WPF host with navigation (Review, History, Settings), MVVM store fed by IPC events, theme switching, global shortcut routing (incl. `?` sheet), service-connection status.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-071, ST-072, ST-073, ST-074, ST-079, ST-080, ST-083
- ⛓️ **Blocked By:** ST-004, ST-016

**Acceptance Criteria:**
- [ ] Service state event → all open views update within 100 ms
- [ ] Service not running → banner "Capture service not running — Start" works
- [ ] Navigation, window size, theme persist; `?` opens shortcut sheet; reduce-motion honored

**Agent brief**
- Read: Spec §2.3 motion, §4 global behaviors, §7
- Write: `/client/ScreenTail.UI/App/*`, `/client/ScreenTail.UI/Shell/*`, `/client/ScreenTail.UI/State/*`
- Verify: `ShellTests`; screenshots both themes
---

---
### **ST-071 · Tray icon, quick menu and "What's being captured" panel**
- **Epic/Feature:** UI
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
Implements Spec §5 S1 exactly: five icon states, menu order and chords, diagnostics panel with Copy diagnostics (states only).

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-112
- ⛓️ **Blocked By:** ST-020, ST-070

**Acceptance Criteria:**
- [ ] `recording` → icon within 200 ms; tooltip "Recording — <tool> · mm:ss"
- [ ] Draft ready → badge count; click opens Review to that draft
- [ ] Panel shows active window + scope, mic, suppression, backlog, Local-only, policy version, CPU/RAM; Copy diagnostics contains no content

**Agent brief**
- Read: Spec §5 S1, §2.4 tray icons, INV-4
- Write: `/client/ScreenTail.UI/Tray/*`, `/client/ScreenTail.UI/Diagnostics/*`, icon assets
- Verify: screenshots of each tray state and the panel
---

---
### **ST-072 · Recording HUD overlay**
- **Epic/Feature:** UI
- **Priority:** Urgent
- **Estimate:** 5

**Description:**
Implements Spec §5 S2: pill with timer, mic meter, redaction count, pause/stop; all state variants; expand/double-click/right-click behaviors; capture-excluded; non-focus-stealing.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-021, ST-029, ST-040, ST-070

**Acceptance Criteria:**
- [ ] HUD appears without taking focus from the remote window
- [ ] Each variant (recording, paused, suppressed ×3, out-of-scope, no mic, offline) renders per Spec with correct token colors
- [ ] HUD pixels absent from frames; position and hidden-state persist; "Mark moment" works from expanded view

**Agent brief**
- Read: Spec §5 S2, §2 tokens, INV-4
- Write: `/client/ScreenTail.UI/Hud/*`
- Verify: screenshots of all variants; capture-exclusion test
---

---
### **ST-073 · Notifications and draft-failure UX**
- **Epic/Feature:** UI
- **Priority:** High
- **Estimate:** 2

**Description:**
Toasts and banners per Spec §6; failure paths always expose the raw timeline.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-064, ST-070

**Acceptance Criteria:**
- [ ] Draft ready toast → Review opens within 1 s
- [ ] `draft_failed` → Review shows timeline/frames/transcript and Retry
- [ ] All Spec §6 messages implemented verbatim; Focus Assist respected

**Agent brief**
- Read: Spec §6
- Write: `/client/ScreenTail.UI/Notifications/*`
- Verify: screenshot of each toast
---

---
### **ST-074 · Review screen — note editor (left pane)**
- **Epic/Feature:** UI
- **Priority:** Urgent
- **Estimate:** 8

**Description:**
Implements Spec §5 S3 left pane: sections, inline editing, reorder, confidence markers with confirm, frame chips, transcript snippets, autosave, banners (partial, purged, local), Discard with typed confirmation. Built against fixtures.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-067, ST-075, ST-078
- ⛓️ **Blocked By:** ST-003, ST-006, ST-015, ST-070

**Acceptance Criteria:**
- [ ] Low-confidence step shows ⚠ with Spec tooltip; clears on edit or `Alt+C`
- [ ] Edits persist within 1 s and survive restart; "Saved/Saving…" indicator
- [ ] Banners render for `partial_capture`, purged frames, local draft
- [ ] Discard requires typing DISCARD; deletes raw data immediately; audit row written
- [ ] Renders fixture with no backend; all Spec §5 S3 shortcuts for this pane work

**Agent brief**
- Read: Spec §5 S3 left pane + shortcuts, §4 undo-over-confirm
- Write: `/client/ScreenTail.UI/Review/NoteEditor/*`
- Verify: `NoteEditorTests`; keyboard-only demo video/screens
---

---
### **ST-075 · Review screen — screenshot strip (center pane)**
- **Epic/Feature:** UI
- **Priority:** High
- **Estimate:** 5

**Description:**
Implements Spec §5 S3 center pane: filmstrip, include/exclude, destructive blur with undo window, delete with undo toast, enlarge with OCR toggle, suppression/scope gap markers.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-078
- ⛓️ **Blocked By:** ST-074

**Acceptance Criteria:**
- [ ] Blur saved → original bytes unrecoverable after undo window
- [ ] Excluded frame never attached on publish
- [ ] Delete → 5 s undo toast then permanent; audit `frame_deleted_by_user`
- [ ] Enlarged view shows cursor marker and OCR text toggle (`T`)

**Agent brief**
- Read: Spec §5 S3 center pane, §3 Filmstrip
- Write: `/client/ScreenTail.UI/Review/Filmstrip/*`
- Verify: `FilmstripTests`; screenshots
---

---
### **ST-076 · Review screen — timeline and transcript panel**
- **Epic/Feature:** UI
- **Priority:** Medium
- **Estimate:** 3

**Description:**
Implements Spec §5 S3 bottom panel: collapsible scrubber, markers, monospace transcript with click-to-jump.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-028, ST-074

**Acceptance Criteria:**
- [ ] Transcript line click → filmstrip highlights frame
- [ ] Redacted text shows `[REDACTED]`
- [ ] `Alt+T` toggles; state persists; markers per Spec

**Agent brief**
- Read: Spec §5 S3 bottom panel, §3 Timeline
- Write: `/client/ScreenTail.UI/Review/Timeline/*`
- Verify: `TimelineTests`
---

---
### **ST-077 · Infer ticket from remote-session window title and clipboard**
- **Epic/Feature:** UI
- **Priority:** Medium
- **Estimate:** 3

**Description:**
Parse title and clipboard at session start for a ticket-ID pattern; pre-select in picker with "Suggested" badge.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-022, ST-092

**Acceptance Criteria:**
- [ ] Title "#48213" → ticket pre-selected with Suggested badge
- [ ] No match → recent tickets as before
- [ ] Clipboard read once at start, never stored

**Agent brief**
- Read: Spec §5 S3 ticket picker
- Write: `/client/ScreenTail.Service/Detection/TicketInference.cs`, picker binding
- Verify: `TicketInferenceTests`
---

---
### **ST-078 · Review screen — ticket picker and publish panel (right pane)**
- **Epic/Feature:** UI
- **Priority:** High
- **Estimate:** 5

**Description:**
Implements Spec §5 S3 right pane: searchable ticket combo, note type, time entry, destinations, Publish with disabled reasons, result list with links and retry.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-093, ST-094, ST-096
- ⛓️ **Blocked By:** ST-017, ST-066, ST-074, ST-075, ST-092

**Acceptance Criteria:**
- [ ] 3+ chars → matches within 1 s showing `#id · summary · company`
- [ ] No ticket → Publish disabled with tooltip "Choose a ticket first"
- [ ] Success → `published` badge, links, "Open in ConnectWise"; partial failure keeps successes and offers Retry
- [ ] No integrations → "Connect a PSA to publish" replaces Publish

**Agent brief**
- Read: Spec §5 S3 right pane and edge states, §6 published toasts
- Write: `/client/ScreenTail.UI/Review/Publish/*`
- Verify: `PublishPanelTests` with fake providers; screenshots of all states
---

---
### **ST-079 · Session history screen**
- **Epic/Feature:** UI
- **Priority:** High
- **Estimate:** 3

**Description:**
Implements Spec §5 S4: virtualized table, filters, coverage banner, bulk discard, audit export, empty state.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-045, ST-070

**Acceptance Criteria:**
- [ ] 200 sessions render < 500 ms
- [ ] Coverage banner reflects filters
- [ ] Bulk discard confirms with count; export contains no content

**Agent brief**
- Read: Spec §5 S4, §3 Table
- Write: `/client/ScreenTail.UI/History/*`
- Verify: `HistoryTests`; screenshots incl. empty state
---

---
### **ST-080 · Settings — Capture**
- **Epic/Feature:** UI
- **Priority:** High
- **Estimate:** 3

**Description:**
Implements Spec §5 S5 sections and copy, including scope warning and hotkey conflict UI.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-083
- ⛓️ **Blocked By:** ST-023, ST-027, ST-070

**Acceptance Criteria:**
- [ ] Tool toggled off → no session starts for it
- [ ] Scope "All windows" → inline warning shown; confirmed before save
- [ ] Live mic meter; changes apply without restart

**Agent brief**
- Read: Spec §5 S5
- Write: `/client/ScreenTail.UI/Settings/Capture/*`
- Verify: screenshots; `SettingsCaptureTests`
---

---
### **ST-081 · Settings — Privacy & Redaction**
- **Epic/Feature:** UI
- **Priority:** High
- **Estimate:** 3

**Description:**
Implements Spec §5 S6: data-flow explainer, Local-only, exclusions, patterns with live regex validator and test box, retention, telemetry with field list, export/delete; admin-locked variants.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-083
- ⛓️ **Blocked By:** ST-042, ST-043, ST-044, ST-046, ST-047

**Acceptance Criteria:**
- [ ] Invalid regex → inline error blocks save
- [ ] Telemetry off → no metrics request on publish (network test)
- [ ] Locked field → read-only with "Set by your admin"
- [ ] Delete everything requires typing DELETE

**Agent brief**
- Read: Spec §5 S6, §3 Toggle locked, INV-11, INV-12
- Write: `/client/ScreenTail.UI/Settings/Privacy/*`
- Verify: screenshots; `SettingsPrivacyTests`
---

---
### **ST-082 · Settings — Integrations**
- **Epic/Feature:** UI
- **Priority:** High
- **Estimate:** 3

**Description:**
Implements Spec §5 S7 cards with Test connection, masked secrets, per-tech member, company mapping editor.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-083
- ⛓️ **Blocked By:** ST-091, ST-095

**Acceptance Criteria:**
- [ ] Valid ConnectWise creds → member and company shown within 3 s
- [ ] Missing clientId → Spec §4 error pattern message naming the fix
- [ ] Secrets masked after save; "Change" reveals

**Agent brief**
- Read: Spec §5 S7, §4 error pattern
- Write: `/client/ScreenTail.UI/Settings/Integrations/*`
- Verify: screenshots; `SettingsIntegrationsTests`
---

---
### **ST-083 · Onboarding wizard**
- **Epic/Feature:** UI
- **Priority:** Medium
- **Estimate:** 5

**Description:**
Implements Spec §5 S8 seven steps; ≤ 5 minutes; re-runnable.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-112
- ⛓️ **Blocked By:** ST-010, ST-021, ST-080, ST-081, ST-082

**Acceptance Criteria:**
- [ ] 3/3 test users reach sample draft ≤ 5 min
- [ ] Skipped integrations → drafts still produced; Publish shows "Connect a PSA"
- [ ] Blocked mic → deep link and re-check

**Agent brief**
- Read: Spec §5 S8
- Write: `/client/ScreenTail.UI/Onboarding/*`
- Verify: screenshots of all 7 steps; timed run log
---

---
### **ST-084 · Accessibility and keyboard audit**
- **Epic/Feature:** UI
- **Priority:** Medium
- **Estimate:** 2

**Description:**
Narrator and keyboard-only audit of all screens against Spec §7; fix findings.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116
- ⛓️ **Blocked By:** ST-078, ST-079, ST-083

**Acceptance Criteria:**
- [ ] Every interactive element has an automation name; logical focus order
- [ ] Accessibility Insights: zero critical findings
- [ ] High-contrast theme mapping verified

**Agent brief**
- Read: Spec §7
- Write: fixes across `/client/ScreenTail.UI/**`, `/docs/ux/a11y-audit.md`
- Verify: Accessibility Insights report attached
---

## Phase 6 — Integrations (Epic: INTEG)

---
### **ST-090 · PSA and documentation-provider interfaces**
- **Epic/Feature:** INTEG
- **Priority:** High
- **Estimate:** 3

**Description:**
`IPsaProvider` / `IDocProvider`, shared DTOs, error taxonomy mapped to Spec §4 messages, contract-test harness with a fake provider.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-091, ST-095, ST-121
- ⛓️ **Blocked By:** ST-008

**Acceptance Criteria:**
- [ ] Interfaces and DTOs documented in `/backend/src/Providers/README.md`
- [ ] Fake provider passes the contract harness in CI
- [ ] Client never references a concrete provider

**Agent brief**
- Read: Spec §4 error pattern
- Write: `/backend/src/Providers/Abstractions/*`, `/backend/tests/Providers/Contract/*`
- Verify: contract tests green with fake
---

---
### **ST-091 · ConnectWise Manage API client**
- **Epic/Feature:** INTEG
- **Priority:** High
- **Estimate:** 5

**Description:**
`IPsaProvider` for ConnectWise PSA REST: Basic auth `companyId+publicKey:privateKey`, `clientId` header, region base URL, backoff, error mapping.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-082, ST-092, ST-093, ST-094
- ⛓️ **Blocked By:** ST-009, ST-090

**Acceptance Criteria:**
- [ ] `GET /system/info` succeeds with valid creds
- [ ] 429 → exponential backoff with jitter, max 3
- [ ] Contract tests pass against recorded mocks; sandbox test documented in `/docs/integrations/connectwise.md`

**Agent brief**
- Read: ST-090 abstractions
- Write: `/backend/src/Providers/ConnectWise/*`, `/docs/integrations/connectwise.md`
- Verify: `backend/tests/Providers/ConnectWise/*`
---

---
### **ST-092 · ConnectWise ticket search**
- **Epic/Feature:** INTEG
- **Priority:** High
- **Estimate:** 2

**Description:**
Search by ID/summary/company via `conditions`; default "recently touched by this API member".

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-077, ST-078
- ⛓️ **Blocked By:** ST-091

**Acceptance Criteria:**
- [ ] "printer" → open tickets newest-first within 1 s p95
- [ ] Numeric → exact ID first
- [ ] Limit 25 with pagination

**Agent brief**
- Read: Spec §5 S3 picker format
- Write: `/backend/src/Providers/ConnectWise/TicketSearch.cs`, endpoint `GET /v1/psa/tickets`
- Verify: provider tests
---

---
### **ST-093 · ConnectWise ticket note push (with screenshots)**
- **Epic/Feature:** INTEG
- **Priority:** High
- **Estimate:** 3

**Description:**
`POST /service/tickets/{id}/notes` + document uploads of selected redacted frames; idempotent via outbox key; configurable footer.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116
- ⛓️ **Blocked By:** ST-078, ST-091

**Acceptance Criteria:**
- [ ] Note text/formatting match editor
- [ ] 3 selected frames → 3 attachments
- [ ] `[REDACTED]` preserved; footer "Drafted with ScreenTail, reviewed by <tech>" configurable

**Agent brief**
- Read: INV-3, INV-1
- Write: `/backend/src/Providers/ConnectWise/Notes.cs`, endpoint `POST /v1/publish/note`
- Verify: provider tests incl. idempotency
---

---
### **ST-094 · ConnectWise time-entry push**
- **Epic/Feature:** INTEG
- **Priority:** High
- **Estimate:** 3

**Description:**
`POST /time/entries` with reviewed duration, work type, member; optional per-tech members; attribution limitation documented.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116
- ⛓️ **Blocked By:** ST-078, ST-091

**Acceptance Criteria:**
- [ ] 30 min → 0.5 h entry with note as description
- [ ] Per-tech member → attributed to that member
- [ ] Failure retries without duplicating the note

**Agent brief**
- Read: ST-093
- Write: `/backend/src/Providers/ConnectWise/TimeEntries.cs`, endpoint `POST /v1/publish/time`
- Verify: provider tests
---

---
### **ST-095 · Hudu API client (with image-upload verification)**
- **Epic/Feature:** INTEG
- **Priority:** High
- **Estimate:** 3

**Description:**
`IDocProvider` for Hudu: companies, connection check, create/update articles, self-hosted URLs; verify image upload support and record approach.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-082, ST-096, ST-097
- ⛓️ **Blocked By:** ST-009, ST-090

**Acceptance Criteria:**
- [ ] `GET /companies` works; cached 10 min
- [ ] Invalid key → "Hudu rejected the API key"
- [ ] Image-upload capability documented in `/docs/integrations/hudu.md`

**Agent brief**
- Read: ST-090
- Write: `/backend/src/Providers/Hudu/*`, `/docs/integrations/hudu.md`
- Verify: provider tests
---

---
### **ST-096 · Hudu KB article publish (draft, company-scoped)**
- **Epic/Feature:** INTEG
- **Priority:** High
- **Estimate:** 3

**Description:**
Note → draft article scoped to mapped company/central KB; images inline if supported else link to ticket attachments.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116, ST-122
- ⛓️ **Blocked By:** ST-078, ST-095

**Acceptance Criteria:**
- [ ] KB on → draft article exists under correct company
- [ ] `kb_candidate: false` → toggle default off with reason
- [ ] Article links to ticket; images render or fallback link works

**Agent brief**
- Read: INV-1
- Write: `/backend/src/Providers/Hudu/Articles.cs`, endpoint `POST /v1/publish/article`
- Verify: provider tests
---

---
### **ST-097 · Map ConnectWise companies to Hudu companies**
- **Epic/Feature:** INTEG
- **Priority:** Medium
- **Estimate:** 3

**Description:**
Per-tenant mapping auto-matched by name with confidence, user-correctable in Settings.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-092, ST-095

**Acceptance Criteria:**
- [ ] Identical names → `confidence: exact`
- [ ] Unmatched → prompt once at publish, remembered
- [ ] Editable in Settings → Integrations

**Agent brief**
- Read: Spec §5 S7 mapping table
- Write: `/backend/src/Mapping/*`, Settings binding
- Verify: `MappingTests`
---

---
### **ST-098 · Metrics ingestion for pilot KPIs**
- **Epic/Feature:** INTEG
- **Priority:** High
- **Estimate:** 2

**Description:**
Anonymized per-session metrics to `session_metrics`; respects telemetry off; offline queue.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116, ST-120
- ⛓️ **Blocked By:** ST-008

**Acceptance Criteria:**
- [ ] No note text, ticket summary, company or transcript in payload (INV-10)
- [ ] Telemetry off → nothing posted
- [ ] SQL view: edit rate and minutes saved per tenant per week

**Agent brief**
- Read: INV-10; Spec §5 S6 telemetry field list
- Write: `/backend/src/Metrics/*`, client `/client/ScreenTail.Service/Metrics/*`
- Verify: schema test rejecting content fields
---

---
### **ST-099 · Web frontend scaffold (tenant onboarding + admin shell)**
- **Epic/Feature:** INTEG
- **Priority:** High
- **Estimate:** 5

**Description:**
React + TS on shared tokens per Spec §5 S9: sign up/in, technicians and seats, policy editor, integration status, client download.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116, ST-120
- ⛓️ **Blocked By:** ST-010, ST-016, ST-047

**Acceptance Criteria:**
- [ ] Owner can sign up, invite a tech, set a policy, see device activate
- [ ] Uses `tokens.json`; light default; matches client
- [ ] Lighthouse accessibility ≥ 95

**Agent brief**
- Read: Spec §5 S9, §2, §7
- Write: `/web/src/**`
- Verify: `npm test`; Lighthouse report; screenshots
---

## Phase 7 — Release, Deployment & Pilot (Epic: RELEASE)

---
### **ST-110 · Stage 0 baseline measurement at the pilot MSP**
- **Epic/Feature:** RELEASE
- **Priority:** Urgent
- **Estimate:** 2

**Description:**
Two-week measurement of documentation minutes per ticket (≥ 3 techs, ≥ 40 tickets) plus 5 interviews; go/no-go and detection priority.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116
- ⛓️ **Blocked By:** None

**Acceptance Criteria:**
- [ ] Baseline recorded
- [ ] Remote-tool usage split recorded
- [ ] Go/no-go note (baseline < 3 min/ticket escalates)

**Agent brief**
- Read: Spec §8
- Write: `/docs/pilot/stage0-baseline.md`
- Verify: doc merged
---

---
### **ST-111 · Pilot agreement, privacy policy, DPA template**
- **Epic/Feature:** RELEASE
- **Priority:** Medium
- **Estimate:** 2

**Description:**
Lightweight pilot agreement, plain-language privacy policy, DPA template based on the data-flow doc.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116
- ⛓️ **Blocked By:** ST-114

**Acceptance Criteria:**
- [ ] Reviewed once by a lawyer or reputable template service
- [ ] Privacy policy at a public URL linked from installer and web
- [ ] First external MSP signs before install

**Agent brief**
- Read: ST-114 output
- Write: `/docs/legal/*`
- Verify: URL live
---

---
### **ST-112 · Signed Windows installer (silent install, auto-start, clean uninstall)**
- **Epic/Feature:** RELEASE
- **Priority:** High
- **Estimate:** 5

**Description:**
EV-signed MSI/EXE; per-user/per-machine; `/quiet`; service at login; uninstall removes binaries, store, tokens, models, tasks.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-113, ST-116
- ⛓️ **Blocked By:** ST-011, ST-071, ST-083

**Acceptance Criteria:**
- [ ] Fresh Win 11 + Defender: no SmartScreen/AV warning
- [ ] `/quiet` completes silently; service starts next login
- [ ] Uninstall leaves nothing; installer < 250 MB excl. models

**Agent brief**
- Read: INV-12
- Write: `/installer/**`, CI release job
- Verify: VM install/uninstall log
---

---
### **ST-113 · Auto-update channel**
- **Epic/Feature:** RELEASE
- **Priority:** Medium
- **Estimate:** 3

**Description:**
Signed updates, stable/beta channels, applied at idle launch, never during a session.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-007, ST-112

**Acceptance Criteria:**
- [ ] Background download; prompt only when idle
- [ ] Signature mismatch rejected and logged
- [ ] Channel selectable in Settings

**Agent brief**
- Read: ST-112
- Write: `/client/ScreenTail.UI/Update/*`, release feed manifest
- Verify: `UpdateTests`
---

---
### **ST-114 · Privacy & data-flow documentation and security questionnaire pack**
- **Epic/Feature:** RELEASE
- **Priority:** High
- **Estimate:** 3

**Description:**
Plain-language data-flow doc with diagram (two-stage frames, scope), technician consent guidance, ≥ 40 pre-filled questionnaire answers referencing the threat model.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-111, ST-116, ST-123
- ⛓️ **Blocked By:** ST-012, ST-045, ST-046

**Acceptance Criteria:**
- [ ] Reviewed by an MSP security lead
- [ ] Consent sheet lists all-party states and script
- [ ] ≥ 40 answers

**Agent brief**
- Read: Guide §3 invariants; Spec §5 S6 data-flow explainer (keep consistent)
- Write: `/docs/security/data-flow.md`, `/docs/security/questionnaire.md`, `/docs/legal/consent-guidance.md`
- Verify: reviewer sign-off noted
---

---
### **ST-115 · Red-team the redaction pipeline**
- **Epic/Feature:** RELEASE
- **Priority:** Urgent
- **Estimate:** 3

**Description:**
≥ 30 adversarial scenarios through the VM harness; fix or document every finding.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-116
- ⛓️ **Blocked By:** ST-013, ST-040, ST-042, ST-043

**Acceptance Criteria:**
- [ ] Zero secrets in any readable frame, transcript or draft
- [ ] Every failure fixed or added to defaults with regression test
- [ ] `/docs/security/redteam-01.md`

**Agent brief**
- Read: INV-1, INV-6
- Write: `/tests/e2e/redteam/*`, report
- Verify: harness run green
---

---
### **ST-116 · End-to-end pilot at partner MSP (M2/M3 exit)**
- **Epic/Feature:** RELEASE
- **Priority:** High
- **Estimate:** 5

**Description:**
3+ techs, ≥ 50 sessions; compare to Stage 0; weekly feedback into backlog.

**Dependencies & Blockers:**
- 🛑 **Blocks:** ST-120, ST-121
- ⛓️ **Blocked By:** ST-031, ST-084, ST-093, ST-094, ST-096, ST-098, ST-099, ST-110, ST-111, ST-112, ST-114, ST-115

**Acceptance Criteria:**
- [ ] Median documentation time reduced ≥ 60% vs baseline
- [ ] Median edit ratio < 25%
- [ ] Zero credential/PII incidents
- [ ] ≥ 80% of observed remote sessions captured

**Agent brief**
- Read: Spec §8
- Write: `/docs/pilot/weekly-*.md`
- Verify: metrics view export
---

---
### **ST-120 · Admin dashboard (web) — v1.1**
- **Epic/Feature:** RELEASE
- **Priority:** Low
- **Estimate:** 8

**Description:**
Per Spec §5 S9 dashboard section; no session content.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-010, ST-047, ST-098, ST-099, ST-116

**Acceptance Criteria:**
- [ ] 8-week charts of minutes saved and edit rate
- [ ] Drafts > 24 h highlighted per tech
- [ ] Seat/policy changes reflect in clients within 5 minutes

**Agent brief**
- Read: Spec §5 S9
- Write: `/web/src/dashboard/**`
- Verify: screenshots; tests
---

---
### **ST-121 · Second PSA connector (HaloPSA) — v1.1**
- **Epic/Feature:** RELEASE
- **Priority:** Low
- **Estimate:** 5

**Description:**
HaloPSA `IPsaProvider` passing contract harness; no Review UI changes.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-090, ST-116

**Acceptance Criteria:**
- [ ] Note and time entry on a HaloPSA ticket
- [ ] Contract harness passes
- [ ] Only Settings PSA type changes

**Agent brief**
- Read: ST-090, ST-091
- Write: `/backend/src/Providers/HaloPsa/*`
- Verify: contract tests
---

---
### **ST-122 · Duplicate-fix detection and KB linking — v1.1**
- **Epic/Feature:** RELEASE
- **Priority:** Low
- **Estimate:** 3

**Description:**
Embed redacted note; search existing Hudu articles; offer "link existing" above threshold.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-096

**Acceptance Criteria:**
- [ ] ≥ 0.85 similarity → "Similar article found" with link option
- [ ] Linking appends ticket reference
- [ ] Embeddings from redacted text only

**Agent brief**
- Read: INV-1
- Write: `/backend/src/Kb/Dedupe/*`, Review KB toggle binding
- Verify: `DedupeTests`
---

---
### **ST-123 · End-user call-audio consent workflow — v1.2**
- **Epic/Feature:** RELEASE
- **Priority:** Low
- **Estimate:** 5

**Description:**
Implements Spec §5 S10; `end_user` diarization; redaction extended; off by default.

**Dependencies & Blockers:**
- 🛑 **Blocks:** None
- ⛓️ **Blocked By:** ST-027, ST-114

**Acceptance Criteria:**
- [ ] No end-user audio until consent confirmed
- [ ] Consent timestamp and script version in audit log
- [ ] Diarization ≥ 90% correct on test call

**Agent brief**
- Read: Spec §5 S10, INV-9 (this ticket amends it under consent)
- Write: `/client/ScreenTail.Service/Speech/EndUserAudio/*`, `/client/ScreenTail.UI/Consent/*`
- Verify: `ConsentTests`
---

## Part C — Sprint plan (solo, ~18–20 pts)

| Sprint | Tickets | Pts |
|---|---|---|
| 1 | ST-001, ST-002, ST-003, ST-004, ST-110 (background) | 16 |
| 2 | ST-005, ST-006, ST-014, ST-016 | 17 |
| 3 | ST-020, ST-021, ST-022, ST-023, ST-015 | 20 |
| 4 | ST-024, ST-025, ST-026, ST-027 (start), ST-017 | 18 |
| 5 | ST-027 (finish), ST-028, ST-029, ST-030, ST-007, ST-011 | 17 |
| 6 | ST-040, ST-041, ST-042, ST-043, ST-012 | 18 |
| 7 | ST-008, ST-060, ST-061, ST-062, ST-013 | 19 |
| 8 | ST-063, ST-090, ST-070, ST-071, ST-066 | 18 |
| 9 | ST-072, ST-074, ST-064, ST-073 — **M1** | 18 |
| 10 | ST-075, ST-009, ST-091, ST-092, ST-044, ST-045 | 20 |
| 11 | ST-078, ST-093, ST-094, ST-095, ST-096, ST-046 — **M2** | 20 |
| 12 | ST-010, ST-047, ST-080, ST-081, ST-082, ST-031 | 20 |
| 13 | ST-112, ST-114, ST-111, ST-115, ST-099, ST-098 | 20 |
| 14 | ST-083, ST-079, ST-084, ST-077, ST-097 — **M3**, pilot begins | 16 |
| 15 | ST-116, ST-065, ST-113, ST-067 | 19 |
| 16+ | ST-120, ST-121, ST-122, ST-123 | 21 |

**Two agents (8 sprints):** Agent A takes ST-020–031, ST-040–047, ST-060–067; Agent B takes ST-001–017, ST-070–084, ST-090–099, ST-110–116. Both on ST-115 and ST-116 in Sprint 8.
