# ScreenTail — Project Scope Document

**Version:** 0.1 — the original scope statement
**Owner:** Hirat
**Date:** September 10, 2026
**Status:** Superseded as the working spec by `00-Build-Agent-Guide.md`, `01-UX-Design-Spec.md` and `02-Backlog-v0.4.md` (2026-09-15). Kept unedited as the statement of goals, success metrics, non-goals and risks. Where §5.3 names libraries (Python, pywinauto, Tesseract, faster-whisper), ADR-0001 decided otherwise: .NET 10, Windows OCR, whisper.net. For where the build stands, read `docs/STATUS.md`.

---

## 1. Summary

ScreenTail is a lightweight desktop app for MSP technicians that watches a remote-support session as it happens — what's on screen, where the cursor goes, and what the technician says — and turns it into a finished ticket note and (optionally) a knowledge-base article. The technician reviews a draft for about 30 seconds instead of writing notes for 5–10 minutes.

The project has two deliverables:

1. **The Tool (capture + intelligence engine)** — a Windows tray app that detects remote-session windows, records clicks and screenshots, transcribes the technician's voice locally, redacts sensitive content, and produces a structured note with one LLM call at session end.
2. **The UI** — the tray app, the post-session Review screen, a Settings/Privacy console, and a small web dashboard for team admins.

**Positioning in one line:** "Documentation that writes itself — for any remote tool, into any PSA."

---

## 2. The Problem

### 2.1 What's happening today
- MSP technicians fix problems over remote tools (ScreenConnect, Splashtop, RDP, TeamViewer, AnyDesk), then reconstruct what they did from memory, screenshots, and scattered notes.
- Industry norms expect roughly four lines of notes per 15 minutes of work. Time not documented is often time not billed.
- Notes are inconsistent across technicians, so knowledge stays in people's heads and onboarding new techs is slow.

### 2.2 Why existing tools don't solve it
| Category | Examples | Gap |
|---|---|---|
| Generic screen-to-guide tools | Scribe, Tango, Guidde, Kommodo | Built for SOPs, not ticket notes. Capture clicks in the local browser/desktop only — they can't see inside a remote-session window. No PSA integration. |
| RMM/PSA-native AI | Atera Copilot, Mizo, SuperOps Monica, ConnectWise Sidekick | Atera summarizes only Splashtop sessions inside Atera. The rest summarize *ticket text*, not the live session. All are locked to one vendor's stack. |
| Direct early mover | Vight (2025, ~2 people) | Same concept; very early. Validates demand; means speed and differentiation matter. |

### 2.3 The gap ScreenTail fills
A **remote-tool-agnostic** and **PSA-agnostic** tool that combines **screen + cursor + voice** and lands the note **where the MSP already works**.

---

## 3. Target Users

| Persona | Who they are | What they need |
|---|---|---|
| **Tier 1/2 Technician (primary)** | 20–100 tickets/week, 10–40 remote sessions/week. Talks to end users on the phone while working. Hates writing notes. | Zero-effort capture, a draft that's right ~75%+ of the time, one-click push to the ticket. |
| **Service Desk Lead / Dispatcher** | Reviews notes for quality and billing; escalations depend on good history. | Consistent note format, visibility into which sessions are documented. |
| **MSP Owner / Operations Manager** | Cares about billable-hours recovery, client-facing professionalism, and vendor risk. | ROI numbers, security assurances (SOC 2 path, local-only mode), per-tech pricing. |
| **Security/Compliance Reviewer** | Approves new vendors; may serve HIPAA/CMMC clients. | Clear data flow, redaction guarantees, no always-on capture, consent handling. |

**Initial target segment:** small-to-mid MSPs (3–30 technicians) on ConnectWise Manage + Hudu, using ScreenConnect or RDP. Expand from there.

---

## 4. Goals and Success Metrics

| Goal | Metric | Target (pilot) |
|---|---|---|
| Cut documentation time | Minutes spent on notes per ticket (measured before/after) | ≥ 60% reduction |
| Notes are good enough to trust | Share of draft text edited before accepting | < 25% |
| Techs actually use it | Sessions auto-captured ÷ total remote sessions | ≥ 80% |
| Nothing sensitive leaks | Credential/PII incidents found in review | 0 |
| Recover billable time | Time entries created from ScreenTail per tech per week | Tracked; used in ROI story |

**Non-goals for v1:** replacing the PSA or RMM, working on macOS/Linux, recording the end user's audio, autonomous remediation, becoming a generic SOP tool.

---

## 5. Product Scope — The Tool

### 5.1 Session lifecycle (how it works, step by step)
1. **Detect** — ScreenTail watches the foreground window. When a known remote-session process gains focus (`mstsc.exe`, `ScreenConnect.WindowsClient.exe`, Splashtop/TeamViewer/AnyDesk clients), it starts a session automatically. A visible indicator appears; a hotkey pauses or stops.
2. **Capture** — Low-level mouse/keyboard hooks log clicks and key events with timestamps. A screenshot of the active window is taken on each click (debounced) plus when the screen changes significantly. The technician's microphone is transcribed locally in real time.
3. **Redact (on device)** — Capture is suppressed while a password field is focused (UI Automation `IsPassword`). OCR scans each frame for patterns (passwords, SSNs, card numbers, API keys) and masks them before anything is stored. User-defined "never capture" apps and windows are honored.
4. **Summarize** — At session end, one LLM call receives the click log, OCR'd text from selected frames, and the transcript, and returns a structured note: **Problem → Steps taken → Result → Follow-ups**, plus a suggested time entry and 3–6 key screenshots.
5. **Review & Publish** — The technician sees the draft, edits inline, picks the ticket, and pushes it. Nothing is published without a human click (v1).
6. **Learn** — Edits the tech makes are stored as style hints so future drafts match how this MSP writes notes.

### 5.2 Functional requirements

**Must have (v1)**
- FR-1 Auto-start/stop on remote-session window focus; manual start/stop hotkey and tray toggle.
- FR-2 Click/keystroke timeline with click-triggered screenshots of the active window.
- FR-3 Local speech-to-text of technician mic (faster-whisper); transcript aligned to click timestamps.
- FR-4 On-device redaction: password-field suppression, regex/OCR masking, app/window exclusion list, "delete this frame" in review.
- FR-5 Structured note generation (Problem / Steps / Result / Follow-ups) with confidence flags on uncertain steps.
- FR-6 Suggested time entry (start/end, duration, rounding rule configurable).
- FR-7 Review screen with inline editing, screenshot picker, ticket search/select.
- FR-8 Push to ConnectWise Manage: ticket note (internal or discussion), time entry.
- FR-9 Publish to Hudu: draft KB article from the note, tagged to company.
- FR-10 Local-only mode: no data leaves the machine (local vision model, reduced quality accepted).
- FR-11 Encrypted local store for raw captures; automatic purge after N days (default 7).
- FR-12 Audit log of what was captured, what was redacted, what was sent, and where.

**Should have (v1.1–v1.2)**
- FR-13 Second PSA (HaloPSA or Autotask) and IT Glue publishing.
- FR-14 Team dashboard (web): coverage %, time saved, edit rate, notes pending review.
- FR-15 Note templates per client/ticket type; per-MSP writing-style profile.
- FR-16 End-user call audio with explicit consent workflow (all-party consent by default).
- FR-17 Duplicate-fix detection: "this looks like KB article #123 — link it instead of writing a new one."

**Could have (later)**
- macOS client; endpoint-side agent for command/registry logs; ScreenConnect extension for start/stop hooks; multilingual transcription.

### 5.3 Architecture (plain-language)
- **Client (Windows tray app):** Python or C#/.NET. Uses pywinauto/pynput for hooks and window detection, `mss` for screenshots, Tesseract or PaddleOCR for text, faster-whisper for speech. Signed installer (code signing is mandatory for MSP trust).
- **Summarization service:** single call per session to a cost-efficient vision-capable LLM (Gemini Flash / GPT-4o-mini class) via a thin backend that also holds PSA/doc-platform connectors. Local-only mode swaps in an on-device model.
- **Backend (v1 minimal):** auth, integration credentials (encrypted), usage metering, dashboard data. Raw screenshots never persist server-side by default; only the redacted bundle passes through and is discarded after the call.
- **Cost envelope:** target < $0.10 per session in model costs by sending few frames and letting voice + click text carry meaning.

---

## 6. Product Scope — The UI

### 6.1 Design principles
1. **Invisible until useful.** The tech should forget it's running — until the draft appears.
2. **Trust through transparency.** Always show what was captured, what was redacted, and where it's going. No hidden capture, ever.
3. **Review in 30 seconds.** The Review screen is the product. Every pixel serves fast scan → tweak → publish.
4. **Feels like MSP software, not a consumer app.** Dense, keyboard-friendly, dark-mode first, no marketing gloss.
5. **Never surprise.** Recording state must be obvious at a glance (Recall backlash lesson).

### 6.2 Style direction
- **Tone:** calm, technical, professional. Think "well-designed SOC tool," not "SaaS landing page."
- **Theme:** dark mode default with a light option; one accent color used sparingly for recording state and primary actions (red = recording, green = published).
- **Typography:** a clean sans-serif for UI (e.g., Inter), monospace for transcript/command text.
- **Density:** compact; tables and lists over cards; keyboard shortcuts on everything in Review.
- **Iconography:** simple line icons; a recognizable tray icon with three states (idle / recording / draft ready).
- **Accessibility:** WCAG 2.1 AA contrast, full keyboard navigation, screen-reader labels — techs may run this all day.

### 6.3 Key screens

| # | Screen | Purpose | Key elements |
|---|---|---|---|
| S1 | **Tray icon + quick menu** | Always-available control | State indicator; Start/Pause/Stop; "Open last draft"; Settings; Privacy: "What's being captured right now?" |
| S2 | **Recording HUD (overlay)** | Make capture state obvious | Small pill on screen edge: timer, mic level, redaction count, pause/stop. Draggable; can be minimized to tray only by explicit choice. |
| S3 | **Review & Publish** | The core workflow | Left: generated note (Problem/Steps/Result/Follow-ups) with inline editing and confidence markers. Center: screenshot strip with select/discard/blur. Right: ticket picker (search PSA), time entry, destination toggles (Ticket note / KB article), Publish button. Bottom: collapsible timeline + transcript with jump-to-moment. |
| S4 | **Session history** | Find and re-open past sessions | Filter by date/client/status (Draft, Published, Discarded); coverage % banner; bulk discard. |
| S5 | **Settings — Capture** | Control what triggers recording | Remote tools to watch (toggle each), hotkeys, screenshot frequency, mic device, auto-start on/off. |
| S6 | **Settings — Privacy & Redaction** | Build trust | Exclusion list (apps/windows/URLs), redaction pattern toggles + custom regex, retention period, Local-only mode switch, "Export my data / Delete everything." |
| S7 | **Settings — Integrations** | Connect once | ConnectWise (company ID, keys, clientId), Hudu (URL, API key); connection test; default note type and time-rounding rule. |
| S8 | **Onboarding wizard** | First-run in < 5 minutes | Permissions explanation (mic, screen), choose remote tools, connect PSA, run a 60-second test session, see a sample draft. |
| S9 | **Admin dashboard (web, v1.1)** | Show ROI to owners | Sessions captured, minutes saved, edit rate, pending drafts by tech, redaction stats; per-tech seat management. |
| S10 | **Consent prompt (v1.2)** | Legal safety for call audio | Clear "record the call?" step with state-law hint and a one-line script the tech can read to the end user. |

### 6.4 Key user flows
- **Happy path:** ScreenConnect window opens → HUD appears → tech works and talks → window closes → toast "Draft ready" → Review (30s) → Publish → ticket note + time entry land in ConnectWise.
- **Sensitive moment:** tech opens a password manager → capture pauses automatically, HUD shows "Paused: sensitive window" → resumes on focus change.
- **Bad draft:** tech marks two steps as wrong, deletes a frame, rewrites Result → edits saved as style feedback → Publish.
- **Missed start:** tech forgot ScreenTail wasn't running → manual Start; partial session still yields a draft with a "partial capture" note.

---

## 7. Constraints

### 7.1 Technical
- **Windows-only for v1** (technician machines); macOS deferred.
- **Remote canvases are opaque.** Inside RDP/ScreenConnect windows there's no accessibility tree, so element names can't be read — OCR and vision must fill in. Expect lower fidelity than local-app capture; design the note generator to lean on voice + click timing.
- **Window detection relies on process names** for most tools (only RDP has a stable window class); each vendor's client update must be re-tested.
- **Compressed remote streams degrade OCR.** Screenshot at native resolution; retry OCR on stabilized frames.
- **Low-level hooks** must respect Windows timeout rules and 32/64-bit requirements; the app must never add perceptible input lag.

### 7.2 Legal and compliance
- **Consent:** twelve US states require all-party consent for audio. v1 records **only the technician's mic**; end-user audio is gated behind an explicit consent flow in v1.2.
- **Data protection:** raw captures encrypted at rest, purged by default in 7 days, never stored server-side; local-only mode available from day one.
- **Vendor requirements:** expect security questionnaires; plan a SOC 2 readiness track after first paying pilots. HIPAA BAA and CMMC considerations for clients in regulated sectors.
- **Code signing** and clear privacy documentation are release blockers.

### 7.3 Business and resourcing
- **Team:** solo builder initially, with access to one MSP for testing. Scope is sized accordingly — one PSA, one doc platform, one OS.
- **Budget:** minimal — LLM/API spend, code-signing certificate, hosting, domain. Target model cost < $0.10/session.
- **Timeline (part-time, indicative):** Stage 0 validation 2 weeks → Stage 1 draft-quality prototype 1–3 months → Stage 2 integrations + redaction 1–2 months → Stage 3 external pilots ongoing.
- **Competitive window:** incumbents (ConnectWise, Atera) could ship native equivalents within 1–2 years; Vight is already in market. Speed and tool-agnosticism are the defense.

### 7.4 Assumptions (to validate in Stage 0)
- Techs at the pilot MSP spend ≥ 5 minutes per ticket on documentation.
- Techs are willing to narrate briefly during sessions when prompted.
- ConnectWise API access (clientId + API member) is available at the pilot MSP.
- A draft with < 25% edits is enough for techs to prefer it over writing from scratch.

---

## 8. Out of Scope (v1)
- macOS/Linux clients
- Recording or transcribing the end user
- Autonomous remediation or scripting
- Endpoint-side agent on client machines
- Multi-PSA / multi-doc-platform support beyond ConnectWise + Hudu
- Generic SOP/training-guide generation
- Mobile apps

---

## 9. Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Sensitive data leaks into a note | Loss of MSP trust; deal-killer | Layered redaction, human review before publish, retention purge, local-only mode, red-team the redactor before pilot |
| Draft quality too low inside remote canvas | Techs stop using it | Voice-first signal, confidence flags, prompt "say what you just did" nudge, iterate on pilot edit data |
| Incumbent ships native feature | Wedge disappears | Tool- and PSA-agnostic, faster iteration, privacy-first differentiation |
| Consent-law misstep | Legal exposure | Tech-mic-only default; consent flow gated in v1.2 |
| Security review blocks adoption | Slow sales | Start with small MSPs, publish data-flow doc early, SOC 2 readiness after first revenue |
| Solo-builder bandwidth | Delays | Ruthless v1 scope; reuse mature libraries; no second integration until metrics hit targets |

---

## 10. Deliverables and Milestones

| Milestone | Deliverable | Exit criteria |
|---|---|---|
| M0 — Validate | Baseline measurement at pilot MSP; 5 tech interviews | Minutes/ticket on docs known; ≥ 3 techs willing to pilot |
| M1 — Capture prototype | Tray app: detection, hooks, screenshots, local transcript; draft note as copyable text | Draft judged "better than what I'd write" by ≥ 2 techs; edit rate < 25% on 20 sessions |
| M2 — Closed loop | Review screen, redaction, ConnectWise + Hudu push, encrypted store | 0 leak incidents in 50 reviewed sessions; ≥ 60% time reduction measured |
| M3 — Pilot-ready | Onboarding wizard, Settings, signed installer, privacy doc, local-only mode | 2–3 external MSPs onboarded via r/msp / peer groups |
| M4 — Team layer | Admin dashboard, second PSA, style profiles | Retention ≥ 80% weekly active techs across pilots |

---

## 11. Open Questions
1. Which remote tool is most common at the pilot MSP — confirms detection priority order.
2. Internal vs. discussion note type default in ConnectWise; time-rounding policy (6 vs 15 min).
3. Do techs want the HUD visible during screen-share with end users, or auto-hidden?
4. Local-only mode: which on-device vision model gives acceptable quality on typical tech laptops?
5. Pricing model: flat per-technician per month vs. per-session — test both in pilot conversations.
