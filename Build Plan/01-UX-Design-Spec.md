# ScreenTail — UX & UI Design Specification (v0.4)

Authoritative for everything a user sees. Build agents implement exactly this; deviations require a design-spec PR first.

---

## 1. Design principles (ranked)

1. **Invisible until useful.** The technician should forget ScreenTail is running until a draft appears.
2. **State is never ambiguous.** Recording, paused, suppressed, out-of-scope, offline, draft-ready — always one glance away.
3. **Review in 30 seconds.** The Review screen is optimized for scan → fix → publish, keyboard-first.
4. **Trust through transparency.** Show what was captured, redacted, purged and sent. Never hide capture.
5. **Calm density.** Compact, quiet, professional. No confetti, no onboarding tours after day one, no motion > 180 ms.
6. **Plain language.** Every label and message is understandable by a first-week tier-1 tech.

---

## 2. Design tokens (`/shared/design/tokens.json`)

### 2.1 Color

| Token | Dark | Light | Use |
|---|---|---|---|
| `bg.base` | `#0B0F14` | `#F6F8FA` | window background |
| `bg.surface` | `#11161D` | `#FFFFFF` | panes, cards |
| `bg.raised` | `#171E27` | `#F0F3F6` | inputs, hovered rows |
| `border.subtle` | `#232C38` | `#D9E0E7` | dividers |
| `border.strong` | `#33404F` | `#B9C3CE` | focused containers |
| `text.primary` | `#E6EDF3` | `#0F1720` | body |
| `text.secondary` | `#9AA7B5` | `#4B5865` | labels, meta |
| `text.muted` | `#66748A` | `#7B8794` | placeholders, timestamps |
| `accent.primary` | `#4F8CFF` | `#2F6FE0` | primary buttons, links, focus ring |
| `accent.primary.hover` | `#6BA1FF` | `#265DBF` | |
| `state.recording` | `#EF4444` | `#DC2626` | recording indicator, stop |
| `state.paused` | `#F59E0B` | `#D97706` | paused / suppressed |
| `state.scope` | `#8B5CF6` | `#7C3AED` | out-of-scope, excluded |
| `state.published` | `#22C55E` | `#16A34A` | success, published |
| `state.offline` | `#94A3B8` | `#64748B` | offline / pending |
| `state.danger` | `#F87171` | `#DC2626` | destructive actions |
| `confidence.low` | `#FBBF24` | `#D97706` | low-confidence step marker |

Rules: one accent only. State colors never appear as decoration. Contrast for all text pairs ≥ 4.5:1 (verified in CI by a token test).

### 2.2 Typography

| Token | Value |
|---|---|
| `font.ui` | Inter, "Segoe UI", sans-serif |
| `font.mono` | "JetBrains Mono", Consolas, monospace |
| `size.xs` | 11 px — timestamps, badges |
| `size.sm` | 12 px — table cells, meta |
| `size.md` | 13 px — **base body** |
| `size.lg` | 15 px — section headings |
| `size.xl` | 20 px — screen titles |
| `weight.regular / medium / semibold` | 400 / 500 / 600 |
| `lineheight` | 1.45 body, 1.25 headings |

### 2.3 Spacing, radius, elevation, motion

- Spacing scale: `4, 8, 12, 16, 24, 32` px. Pane padding 16; control padding 8×12; list row height 32.
- Radius: `4` controls, `6` cards, `999` pills.
- Elevation: none in dark theme except HUD and toasts (`0 4px 16px rgba(0,0,0,.45)`); light theme uses `1px` borders plus `0 1px 3px rgba(15,23,32,.08)`.
- Motion: `120 ms` state changes, `180 ms` pane open/close, `ease-out`. No bounce, no parallax. Respect "reduce motion" OS setting.

### 2.4 Iconography
Lucide icon set, 16 px in controls, 20 px in navigation. Tray icons are custom 16/32 px: a rounded "tail" glyph in `text.secondary` (idle), `state.recording` with dot (recording), `accent.primary` with badge (draft ready).

---

## 3. Component library (WPF; web mirrors)

| Component | Variants | Notes |
|---|---|---|
| Button | primary, secondary, ghost, danger | 32 px tall; icon + label; disabled shows reason in tooltip |
| Input / Search | default, error | inline validation message below, never a dialog |
| Toggle | default, locked (admin) | locked shows lock icon + "Set by your admin" tooltip |
| Select / Combo | searchable | used for ticket picker, mic device |
| Table | virtualized, sortable | 32 px rows, sticky header |
| Badge | state, confidence, suggested | pill, 11 px, uppercase off |
| Toast | info, success, warning, error | bottom-right, 6 s, action button optional |
| Banner | info, warning | inline at top of a pane; dismissible when informational |
| Modal | confirm, destructive | only outside recording; destructive requires typed confirmation for irreversible data loss |
| Split pane | 3-pane, resizable | persists sizes |
| Filmstrip | thumbnails 160×90 | selection, exclude, blur, delete |
| Timeline | horizontal scrubber | markers for clicks, frames, narration, suppression, scope |
| Status pill (HUD) | recording/paused/suppressed/scope/offline | see §5 |

Focus ring: 2 px `accent.primary` outside the control; visible for keyboard focus only.

---

## 4. Global behaviors

- **Keyboard first.** Every action reachable without mouse; shortcuts listed per screen; `?` opens a shortcut sheet.
- **Never interrupt recording.** No modals, no focus-stealing while a session is recording. Warnings go to the HUD.
- **Undo over confirm** for reversible actions (exclude frame, reorder step). Typed confirmation only for irreversible deletes.
- **Errors say what happened and what to do.** Pattern: `<What happened>. <What to do>.` Example: "ConnectWise rejected the request: clientId header missing. Add your clientId in Settings → Integrations."
- **Time** shown relative in lists ("14 min ago") with absolute on hover.
- **Empty states** always contain one primary action.
- **Copy voice:** plain, present tense, no exclamation marks, no "Oops". Use "note" not "documentation", "session" not "recording" in tech-facing copy, "capture" for the act.

---

## 5. Screens

### S1 — Tray icon and quick menu

**States:** idle · recording (red dot, tooltip "Recording — ScreenConnect · 12:41") · draft ready (badge count) · offline (grey) · service stopped (warning glyph).

**Menu (top → bottom):**
```
● Recording — ScreenConnect (12:41)      [state line, non-interactive]
  Pause                        Ctrl+Alt+P
  Stop and draft               Ctrl+Alt+S
  Discard session…
──────────────────────────────
  Open last draft (2 ready)
  Session history
  What's being captured right now?
──────────────────────────────
  Settings
  Quit ScreenTail
```
When idle, the first block becomes `Start capture   Ctrl+Alt+R`.

**Discard session… while recording:** choosing it stops capture first (the session moves to `finalizing`), then shows the typed-confirmation dialog ("DISCARD"). No modal ever appears while a session is recording. *(v0.4.1, Q3)*

**"What's being captured right now?" panel** (small window, 360×420): active window name and scope status; mic device + live level; suppression state; redaction backlog count; Local-only badge; policy version; CPU/RAM; a "Copy diagnostics" button (no content, only states).

### S2 — Recording HUD

Draggable pill, `999` radius, 44 px tall, docked by default to the top-right edge, `bg.surface` with `border.strong`, elevated. Always on top, does not take focus, excluded from capture.

```
┌────────────────────────────────────────────────────────┐
│ ● 12:41   ▂▄▆ mic   ⛨ 3 redacted   [ ‖ Pause ] [ ■ Stop ] │
└────────────────────────────────────────────────────────┘
```

**State variants (left segment):**
- Recording: red dot + timer.
- Paused: amber `‖ Paused — press Ctrl+Alt+P to resume`.
- Suppressed: amber `⏸ Paused: sensitive field` / `Paused: excluded app` / `Paused: elevated window` — auto-resumes. The elevated-window tooltip reads "Elevated window — screen not captured." *(v0.4.1, Q5)*
- Out of scope: violet `Not capturing — Outlook` (clicks logged, no frames).
- Mic missing: grey mic glyph with tooltip "No microphone. Capture continues without voice."
- Offline: grey cloud glyph; tooltip "Offline — draft will be created when connected."

**Interactions:** click pill → expand to show last 3 timeline events and "Mark moment" (Ctrl+Alt+M). Double-click → open Review of current session (read-only until stopped). Right-click → hide HUD for this session (tray remains). Position persists.

**Screen sharing:** the HUD stays visible to the technician at all times, including while they share their screen with a customer. `WDA_EXCLUDEFROMCAPTURE` keeps it out of screen-share and recording apps, so viewers never see it (verified in ST-001 AC3). There is no auto-hide, because that would create a silent-capture path (INV-4). *(v0.4.1, Q1)*

### S3 — Review & Publish (the product)

Window default 1280×800, minimum 1024×680. Three resizable panes plus a collapsible bottom panel.

```
┌ Title bar: "Review — Printer offline · Acme Dental · 12 min · Sep 10, 14:02"     [Discard] [Publish ▸] ┐
├──────────────────────────────┬──────────────────────────────┬──────────────────────────────────────┤
│ NOTE (editable)              │ SCREENSHOTS (7 of 14)        │ PUBLISH                              │
│                              │                              │                                      │
│ Problem                      │ ┌────┐ ┌────┐ ┌────┐ ┌────┐  │ Ticket                               │
│ [text…]                      │ │ 1  │ │ 2  │ │ 3  │ │ 4  │  │ [🔍 #48213 Printer offline — Acme ] │
│                              │ └────┘ └────┘ └────┘ └────┘  │  Suggested from window title  ✓      │
│ Steps                        │  ✓      ✓      ✓      –      │                                      │
│ 1. Checked spooler service   │ ┌────┐ ┌────┐ ┌────┐         │ Note type   (•) Internal ( ) Discussion│
│    — stopped [frame 2]       │ │ 5  │ │ 6  │ │ 7  │         │ Time entry  [ 0:30 ] ▾  (rounded 15) │
│ 2. ⚠ Restarted spooler       │ └────┘ └────┘ └────┘         │                                      │
│    (inferred from screen)    │                              │ Destinations                         │
│ 3. Cleared print queue       │ [Include] [Blur…] [Delete]   │ [x] Ticket note                      │
│    "clearing the queue now"  │                              │ [ ] KB article  (not a KB candidate) │
│ ⋮ drag to reorder            │                              │                                      │
│                              │                              │ ───────────────────────────────────  │
│ Result                       │                              │ [        Publish   Ctrl+Enter      ] │
│ [text…]                      │                              │                                      │
│                              │                              │ After publish: links to note, time   │
│ Follow-ups                   │                              │ entry, article + "Open in ConnectWise"│
│ [text…]                      │                              │                                      │
├──────────────────────────────┴──────────────────────────────┴──────────────────────────────────────┤
│ ▸ Timeline & transcript  ─●──●───●●─────●──[▮▮ suppressed]──●──   14:02:10  "clearing the queue now" │
└──────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Left pane — Note editor**
- Sections fixed in order: Problem, Steps, Result, Follow-ups. Section headings are `size.lg` semibold, `text.secondary`.
- Steps are a list; each item: text (inline editable), `frame_refs` chips ("frame 2") that highlight the filmstrip on hover and jump on click, an optional quoted transcript snippet in `font.mono` `text.muted`.
- Low-confidence steps show a `confidence.low` ⚠ marker with tooltip "Inferred from screen only — please verify". Marker clears when the step is edited or explicitly confirmed (`Alt+C`).
- Drag handle on hover; `Alt+↑/↓` reorders. `Enter` at end of a step creates a new step; `Backspace` on empty deletes.
- Autosave indicator bottom-left of pane: "Saved" / "Saving…" in `text.muted`.
- Banners at top of pane when applicable: *Partial capture* ("Capture started late or was interrupted. Some steps may be missing."), *Frames purged* ("3 screenshots were removed because they could not be redacted in time."), *Local draft* ("Drafted on this device only — quality may be lower.").

**Center pane — Screenshots**
- Filmstrip grid, 160×90 thumbnails, ordered by time. Included frames have a check; excluded are dimmed. Suppressed/out-of-scope intervals appear as slim striped gaps with a tooltip.
- Toolbar: Include/Exclude (`Space`), Blur… (`B`, opens region-draw overlay on the enlarged frame; destructive, with one-step undo until pane loses focus), Delete (`Del`, typed confirmation not required but a 5 s undo toast is shown; deletion becomes permanent after), Enlarge (`Enter`).
- Header shows "7 of 14 included". Enlarged view shows cursor marker and OCR text toggle (`T`) for verification.

**Right pane — Publish**
- Ticket picker: searchable combo, shows `#id · summary · company`; "Suggested" badge when inferred; recent tickets listed on focus.
- Note type radio, default **Internal**: Discussion notes are customer-visible in ConnectWise, and a first draft shouldn't default to customer-facing. Tenants can change the default in Settings → Integrations. *(v0.4.1, Q2)* Time entry field with rounding note; editable.
- Destinations: checkboxes; KB toggle default follows `kb_candidate` with reason text.
- Primary **Publish** button; disabled state tooltip explains why ("Choose a ticket first").
- After publish: pane switches to a result list with green checks, links, and "Retry" on any failed destination; the title bar gains a `Published` badge.

**Bottom panel — Timeline & transcript**
- Collapsed by default to a 40 px scrubber; `Alt+T` toggles. Expanded: 220 px with monospace transcript lines `[14:02:10] clearing the queue now`; clicking a line highlights its frame; markers for clicks (dots), frames (squares), suppression (striped), scope (violet band), "mark moment" (flag).

**Header actions:** `Discard` (ghost, danger on hover → typed confirmation "DISCARD"), `Publish ▸` mirrors the right pane's button.

**Shortcuts:** `Ctrl+Enter` Publish · `Ctrl+S` save now · `Alt+1/2/3` focus pane · `Alt+T` timeline · `Alt+C` confirm step · `Alt+↑/↓` reorder · `Space` include/exclude · `B` blur · `Del` delete frame · `?` shortcut sheet · `Esc` close enlarged frame.

**Empty/edge states:** draft failed → note pane shows "We couldn't draft this session" with Retry and the timeline/transcript still available, and Publish stays disabled with the reason "There is no note to publish yet. Retry the draft first." (writing a note by hand after a failed draft is v1.1) *(v0.4.1, Q4)*; offline → "Draft pending — offline" banner and disabled Publish with reason; no integrations → Publish replaced by "Connect a PSA to publish" linking to Settings.

### S4 — Session history

Table: Status (badge) · Started · Duration · Tool · Client/ticket (if published) · Frames · Actions. Filters: date range, status (Draft, Published, Discarded, Partial, Pending), tool. Coverage banner: "Captured 46 of 52 remote sessions this week (88%)". Row click opens Review. Multi-select → Bulk discard (typed confirmation: type the number of selected sessions, e.g. `3`) *(v0.4.1, Q6)*. "Export audit log" button (JSON/CSV; no content). Empty state: "No sessions yet. Start a remote session and ScreenTail will capture it." with a Start capture button.

### S5 — Settings → Capture

Sections: *Remote tools* (list with toggles per tool and browser pattern; "Add custom…"); *Capture scope* (radio: Remote-tool windows + admin tools (recommended) / All windows during a session — selecting "All" shows an inline warning about other customers' data); *Auto-start* toggle and grace period slider (30–300 s); *Hotkeys* (editable chords with conflict detection); *Screenshots* (scene-change sensitivity Low/Med/High); *Microphone* (device combo, live level meter, model size Small/Medium with size and accuracy hint); *Start UI at login* toggle.

### S6 — Settings → Privacy & Redaction

Sections: *What leaves this device* (a short data-flow diagram in plain language: captured on device → redacted on device → summary request (or local model) → you publish); *Local-only mode* toggle (locked if policy) with quality note; *Excluded apps and windows* (list, add process/title/URL; defaults labeled "Recommended"); *Redaction patterns* (toggles: SSN, cards, API keys, passwords; custom regex with live validator and test box); *Retention* (slider 1–30 days; shows what is deleted vs kept); *Usage telemetry* (toggle with the exact list of fields sent); *Your data* (Export audit log · Delete everything — typed "DELETE").

### S7 — Settings → Integrations

Cards per integration: **ConnectWise Manage** (site/region, company ID, public key, private key, clientId; Test connection → shows member and company; default note type; time rounding 6/15; optional per-technician API member), **Hudu** (base URL, API key; Test connection; central vs company-scoped KB), **Company mapping** (table CW company ↔ Hudu company, auto-matched with confidence badge, editable). Secrets masked after save; "Change" reveals fields.

### S8 — Onboarding wizard (first run)

Steps, progress dots at top, ≤ 5 minutes:
1. **Welcome** — one sentence of what ScreenTail does; "Nothing is published without your review."
2. **Activate** — enter device code from invite; shows tenant name on success.
3. **Permissions** — microphone (with Windows deep link), capture check, hook check; each shows ✓ or a fix.
4. **Your remote tools** — detected tools pre-checked; scope explanation with recommended default.
5. **Connect** — ConnectWise and Hudu (skippable: "You can publish later").
6. **Try it** — 60-second guided test: open any window, click around, say one sentence; see a sample draft in a mini Review.
7. **Done** — "ScreenTail starts when you open a remote session. Look for the pill." Button: Finish.

Re-runnable from Settings → Help.

### S9 — Web admin (tenant onboarding + dashboard shell)

Pages: Sign up / Sign in · Technicians (invite, seats used/total, device status) · Policy (retention, Local-only enforce, exclusion defaults, redaction patterns, capture scope, telemetry) · Integrations status · Download client · (v1.1) Dashboard: minutes saved/week, edit rate, pending drafts by tech, coverage, redaction stats, remote-tool version drift. Same tokens; light theme default on web; dense tables; no marketing chrome inside the app.

### S10 — Consent prompt (v1.2)

Appears only when end-user audio is enabled. Modal before recording end-user audio: state-law hint based on tenant/technician state, a one-line script ("This session may be recorded for documentation. Is that okay?"), buttons "Consent given — record" / "Skip end-user audio". Decision and script version written to the audit log.

---

## 6. Notifications and microcopy

| Event | Toast text | Action |
|---|---|---|
| Draft ready | "Draft ready — Printer offline · Acme Dental" | Review |
| Draft pending offline | "Offline — your draft will be created when you're back online." | — |
| Draft failed | "We couldn't draft this session. Your screenshots and transcript are saved." | Open |
| Cost cap reached | "Cloud drafting paused for today. This draft was created on your device." | Review |
| Published | "Published to ticket #48213 · 0:30 logged" | Open in ConnectWise |
| Partial publish | "Note published. Time entry failed: <reason>." | Retry |
| Capability | "Microphone is blocked by Windows privacy settings." | Fix |

Copy rules: sentence case; no exclamation marks; name the object (ticket, note, session); always offer the next step.

---

## 7. Accessibility

- WCAG 2.1 AA contrast for all text; verified by a token test in CI.
- Every interactive element has an `AutomationProperties.Name`; lists announce count and position.
- Full keyboard path on every screen; focus order follows visual order; focus ring visible.
- Respect OS "reduce motion" and high-contrast themes (map tokens to system colors when high contrast is on).
- Minimum hit target 28×28 px; HUD controls 32×32.
- Screen-reader text for state pills ("Recording, 12 minutes 41 seconds, 3 items redacted").

---

## 8. Usability metrics the design must hit (measured in usability round and pilot)

- Time from "Draft ready" toast to Publish: median ≤ 45 s, p75 ≤ 90 s.
- Edit ratio on accepted drafts: < 25%.
- Zero occurrences of "I didn't know it was recording" in pilot interviews.
- Onboarding completion ≤ 5 minutes for 3/3 test users.
- Task success without help: pick ticket, exclude a frame, blur a region, change time entry, publish — ≥ 90% across 5 techs.

---

## 9. Amendments

| Version | Date | Source | Change |
|---|---|---|---|
| v0.4.1 | 2026-09-11 | ST-014 wireframes, owner decision | Q1 HUD always visible while screen-sharing (S2). Q2 default note type Internal (S3). Q3 tray discard while recording stops capture before confirming (S1). Q4 Publish disabled after a failed draft (S3). Q5 elevated-window HUD wording and tooltip (S2). Q6 bulk-discard confirmation is the session count (S4). Background: `docs/ux/wireframes/README.md`. Q1 and Q2 are still to be checked in the ST-014 technician sessions. |
