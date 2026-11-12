# ScreenTail: UX & UI Design Specification (v0.6.4)

Authoritative for everything a user sees. Build agents implement exactly this; deviations require a design-spec PR first.

**Targets and the current build (v0.6.0 to v0.6.4).** v0.6.0 brings in the owner-approved brand (Iris, the Ribbon direction) and layout (Concept C, "Outbox"). The tokens in §2 ship with v0.6.0. The layouts marked **Target (v0.6.0)** in §3 and §5 are what later screen PRs build, one screen at a time; until a screen's PR lands, the build keeps that screen's v0.5 layout, which stays described beside the target. Every behaviour rule in §4 and §5 (publish gating, not confirmed, no dialog while capturing, keyboard paths, copy) applies to both. **Built so far:** S3's envelope header and receipt (v0.6.1); the workspace frame and S4's inbox rail (v0.6.4). The note as it lands keeps its v0.5 layout, and the pill and Settings are in their own screen PRs.

---

## 1. Design principles (ranked)

1. **Invisible until useful.** The technician should forget ScreenTail is running until a draft appears.
2. **State is never ambiguous.** Recording, paused, suppressed, out of scope, offline, draft ready: always one glance away.
3. **Review in 30 seconds.** The Review screen is optimized for scan, fix, publish, keyboard first.
4. **Trust through transparency.** Show what was captured, redacted, purged and sent. Never hide capture.
5. **Calm density.** Compact, quiet, professional. No confetti, no onboarding tours after day one, no motion over 180 ms.
6. **Plain language.** Every label and message is understandable by a first-week tier-1 tech.

### 1.1 Layout principles (v0.6.0, Concept C "Outbox")

Publishing works like sending an email: sessions wait in an inbox, the note is written exactly as it will land, and a header of four lines says where it goes.

- **The note is the screen.** Technicians edit the note as it will land on the ticket. There is never a second copy to keep in sync.
- **Evidence sits beside the claim.** A step's frame and quote are on the step's own row. Nothing asks the tech to match numbers across columns.
- **The destination is a value, not a search box.** "To" shows the bound ticket with Change. Publish stays disabled until a ticket is bound, and the reason is written beside it.
- **Check, then Ctrl+Enter.** Defaults show as values. Changing one takes one action. Nothing that changes where the note goes or who can read it is ever hidden behind Options.
- **Sessions are an inbox, grouped by what they need:** To check, Not confirmed, Published today, Earlier. Company, title and ticket lead every row; tool and duration follow.
- **Every ending is a receipt.** After publishing, show what landed where, name each provider on its link, and put the next session one key away.
- **The pill controls; it never shows content.** Pause, Stop and Mark are one click. The expanded pill lists event types and times only. It is always visible to the technician.
- **One Save per page, never a dialog.** Changes collect in a bar that names each one. Leaving the page keeps them pending. Test connection never saves.
- **State is a word, an icon and a colour.** On a tinted fill the word stays in body colour and the icon carries the hue. In high contrast the word and icon remain.
- **Iris means you can act.** Red, amber, green, slate teal and blue slate mean state. Neither is used for the other.
- **Nothing interrupts a recording.** Every question is asked inline, beside the thing it is about.

---

## 2. Brand and design tokens (`/shared/design/tokens.json`)

### 2.0 Brand (v0.6.0)

The brand is **Iris #6A43C4** (Ribbon 600, OKLCH 0.50 0.19 292) on an **indigo-navy frame #141030**, chosen by the owner. The logo files are in `shared/design/brand/`, and its README says which file goes where.

**Palette.** Every brand colour is a token (§2.1); nothing in the app uses a brand hex directly.

| Name | Hex | Where |
|---|---|---|
| Frame, indigo navy | `#141030` | the app frame in light (rail, title bar, pill: `shell.bg`), the app icon tile, installer and website hero |
| Ribbon 600, Iris | `#6A43C4` | the mark; light-theme action (`accent.primary`), links, focus, selection band, info |
| Ribbon 700 | `#572FA7` | light hover and pressed (`accent.primary.hover`); the flat tone step of a fold |
| Ribbon 400 | `#B2A6EC` | dark-theme action, link and focus, with ink text on fills; the tray glyph on the dark taskbar; the small glyph on the frame |
| Ribbon 300 | `#CBC3F5` | dark-theme hover and pressed |
| Ribbon 100 | `#F0EDFE` | light selection and info tint (`bg.selected`) |
| Ribbon 900 | `#2B1D50` | light frame hover (`shell.hover`) |
| Paper | `#F7F5FE` | light `bg.base`, the ribbon underside on the frame, text on the frame |
| Ink | `#0A071E` | the screen in the full mark; text on Ribbon 400 and 300 fills (`text.on-accent`, dark) |

**Colour rules.**
- **One brand hue.** Iris and its tints are the only brand colour. `accent.primary`, `brand.signature` and `brand.ribbon.band` are one value per theme (a token test holds this).
- **Flat fills.** No gradients, glows, blur, Mica or acrylic, in the app or in the logo. Where a fold needs depth it takes one flat tone step (`brand.ribbon.fold`).
- **"Act here" follows the brand.** Primary buttons, links, keyboard focus, checked boxes and radios, the selected row's band and the info banner are Iris in light and Ribbon 400 in dark. Nothing else is Iris.
- **State is a word, an icon and a colour, never colour alone.** Recording and danger are red, paused and inferred amber, published green, out of scope slate teal, offline blue slate. On a tinted fill the word is `text.primary` and the icon or border carries the hue.
- **Out of scope moved off violet.** The v0.5 violet (`#7442E0`) was ΔE 4.6 from Iris and would read as "act here", so out of scope is slate teal, `#3E6E7A` light and `#7CCDD6` dark (ΔE 18.5 and 13.1 from the brand). It keeps its icon (eye-off) and its word ("Not capturing"). Offline keeps its v0.5 blue slate (`#6A7380` / `#A2AAB5`), ΔE 6.0 and 9.6 from out of scope.
- **Never recolour the ribbon in a state colour.** State goes in a badge on the mark, never in the ribbon.
- **Hudu.** Hudu's approximate brand violet `#4D2AC9` is ΔE 7.3 from Iris. That is accepted: "Open in Hudu" is plain `accent.primary` link text with the external-link glyph, and the app draws no Hudu logo, wordmark or Hudu colour, so the link reads as ScreenTail's own action.

**Logo usage.**
- **Grounds.** The frame, white and paper only. On a photo, put the mark on a frame or white plate. Never on mid violet: the ribbon dissolves into it.
- **Wordmark.** Exo 2 ExtraBold converted to outlines (SIL Open Font License 1.1), tracking -20/1000. On the frame the whole word is white (18.32:1); Iris is only 2.80:1 there. On white, "Screen" is frame-coloured and "Tail" is Iris (6.54:1, large text). Exo 2 is for the website and installer headings only; the app stays in Segoe UI Variable.
- **Size ladder.** Never shrink the full mark into a small slot; switch files by size.

| Rendered size | File |
|---|---|
| 56 px and up | full mark: `mark-primary-on-navy.svg`, `mark-primary-on-white.svg`, `mark-primary-transparent-light.svg` (light surfaces in the app, such as the receipt) |
| 28 to 48 px | mid glyph, two note lines: `glyph-mid-on-navy.svg` |
| 16 to 24 px | small glyph, ribbon and solid screen only: `glyph-small-light.svg`, `glyph-small-dark.svg` (the title bar glyph on the frame) |
| app icon | `app-icon-256.svg` from 32 px, `app-icon-small.svg` at 16 to 24 px. The .ico packs the small glyph at 16, 20 and 24, the mid glyph at 32 and 48, the full mark at 64, 128 and 256 |
| lockups | horizontal (`lockup-horizontal-on-navy.svg`, `-on-white.svg`) with the mark at least 24 px tall (about 120 px wide); stacked (`lockup-stacked-on-navy.svg`, `-on-white.svg`) with the mark at least 64 px |
| print | one colour, black, white or frame: `mark-mono-black.svg`, `mark-mono-white.svg`, `lockup-horizontal-mono-black.svg`; the screen stays knocked out |

- **Clear space.** One band width on every side: 12 of the 64 units, or 3/16 of the mark's width. In lockups the gap between mark and wordmark is 18 units, and nothing else enters the box.
- **Don't** stretch, skew, tilt or rotate it: the point symmetry is the identity.

**Tray glyph tiers.** The tray uses the small glyph at 16 px (100% scaling) and 32 px (200%), one file per state and taskbar (`tray-<state>-<light|dark>-<16|32>.svg`). Idle is Iris on the light taskbar (5.89:1 on `#F3F3F3`) and Ribbon 400 on the dark one (7.42:1 on `#202020`), because Iris is only 2.49:1 there. Each state adds a shape, cut in with a transparent ring, so none relies on colour: recording a filled dot (`#D3302F` light, `#FF6B6B` dark), paused two bars (`#B35900`, `#F5A524`), offline a grey blue-slate glyph and a cross (`#6A7380`, `#A2AAB5`). Draft ready keeps the idle glyph with a count badge (S1). The app's `.ico` and tray resources move to these files in a follow-up PR.

### 2.1 Color

Values as of v0.6.0. `shared/design/tokens.json` is the source of truth; this table restates it. Each token also names the system colour it takes in high contrast (`hc`), which `Tokens.HighContrast.xaml` maps by hand.

| Token | Dark | Light | Use |
|---|---|---|---|
| `bg.base` | `#161C2A` | `#F7F5FE` | window background, the bottom layer; light is paper |
| `bg.surface` | `#1B2231` | `#FFFFFF` | panes, cards: the layer content sits on |
| `bg.raised` | `#222A3B` | `#F5F7FA` | inputs and secondary buttons at rest |
| `bg.hover` | `#2A3346` | `#ECEEF4` | a hovered control, row or list item |
| `bg.pressed` | `#1F2636` | `#F0F2F7` | a pressed control |
| `bg.selected` | `#343049` | `#F0EDFE` | a selected row, list item or nav tab; the info banner (iris tint) |
| `bg.warning` | `#352A18` | `#FFF3D6` | warning banner, low-confidence badge |
| `bg.danger` | `#3A2226` | `#FDECEC` | error banner, hovered destructive button |
| `bg.success` | `#163423` | `#E3F4E8` | published badge |
| `border.subtle` | `#2A3242` | `#DADEE6` | dividers, card outlines, a disabled input's outline |
| `border.strong` | `#39404F` | `#BFC4CF` | button and container outlines, focused containers |
| `border.control` | `#747A89` | `#747A89` | the edge of a text box, password box or combo box |
| `text.primary` | `#ECEEF4` | `#121826` | body |
| `text.secondary` | `#BFC4CF` | `#39404F` | labels, section headings, meta |
| `text.muted` | `#999EAB` | `#545B6A` | placeholders, timestamps, hints |
| `accent.primary` | `#B2A6EC` | `#6A43C4` | primary buttons, links (incl. "Open in Hudu"), focus ring, checked, selection, info: Iris |
| `accent.primary.hover` | `#CBC3F5` | `#572FA7` | hovered and pressed primary |
| `text.on-accent` | `#0A071E` | `#FFFFFF` | text on `accent.primary`; dark uses ink on the light iris tint |
| `brand.signature` | `#B2A6EC` | `#6A43C4` | non-text brand iris: rules and marks, at 3:1 |
| `brand.ribbon.band` | `#B2A6EC` | `#6A43C4` | the 4 px band over the selected row, the pill cap, the receipt rule |
| `brand.ribbon.fold` | `#6A43C4` | `#572FA7` | the one flat tone step where the band folds into the envelope (decorative) |
| `state.recording` | `#FF6363` | `#D3302F` | recording indicator, stop |
| `state.paused` | `#F5A524` | `#B35900` | paused / suppressed |
| `state.scope` | `#7CCDD6` | `#3E6E7A` | out of scope, excluded: slate teal *(v0.6.0, was violet)* |
| `state.published` | `#4CD787` | `#167A3B` | success, published |
| `state.offline` | `#A2AAB5` | `#6A7380` | offline / pending: blue slate, unchanged |
| `state.danger` | `#FF8F8F` | `#C42B1C` | destructive actions |
| `confidence.low` | `#FBBF24` | `#B35900` | low-confidence step marker |
| `shell.bg` | `#121826` | `#141030` | the frame: rail, title bar, pill |
| `shell.hover` | `#1C2333` | `#2B1D50` | a hovered rail row, the live box |
| `shell.text` | `#F7F5FE` | `#F7F5FE` | text on the frame |
| `shell.muted` | `#BFC4CF` | `#BFC4CF` | meta and control edges on the frame |
| `shell.accent` | `#B2A6EC` | `#B2A6EC` | focus and links on the frame |
| `shell.line` | `#2A3242` | `#2B2A46` | rail dividers |
| `shell.recording` | `#FF6B6B` | `#FF6B6B` | recording on the frame |
| `shell.paused` | `#F5A524` | `#F5A524` | paused on the frame |
| `shell.published` | `#55DB8E` | `#55DB8E` | published on the frame |
| `shell.scope` | `#7CCDD6` | `#7CCDD6` | out of scope on the frame |
| `shell.offline` | `#A2AAB5` | `#A2AAB5` | offline on the frame |

**Contrast rules, all enforced by `ScreenTail.Tests/Theme/ContrastTests`, which reads `tokens.json` directly.** Every token is in a list that says what it may do, or the test fails.
- Text (`text.*`, `accent.primary`) is at least 4.5:1 on every content background, the tints included, in both themes. The tightest light pair is `accent.primary` on `bg.hover`, 5.63:1; the tightest dark pair is `text.muted` on `bg.selected`, 4.70:1. `text.on-accent` is 6.54:1 (light) and 9.00:1 (dark) on the accent and more on its hover.
- State colours, `brand.signature` and `brand.ribbon.band` are indicators: at least 3:1 on every content background. They are dots, glyphs, rules and borders, never body-size text, so a state is always spelled out in `text.primary` next to its glyph. *(v0.4.2)*
- On the frame, `shell.text`, `shell.muted` and `shell.accent` are at least 4.5:1 on `shell.bg` and `shell.hover` (the tightest is `shell.accent` on light `shell.hover`, 6.86:1), and the `shell.*` states and `shell.accent` at least 3:1.
- `border.subtle`, `border.strong`, `brand.ribbon.fold` and, on the frame, `shell.line` are structural: dividers and outlines that are never the only cue, held only to differing from what they sit on.
- **The band on the frame is decorative.** Where `brand.ribbon.band` meets `shell.bg` (the light pill cap) it is the logo case, Iris on the frame at 2.80:1, deep by design. It is never the only cue there: the pill has its own outline and words, and high contrast does not show it. It is held only to differing from the frame, and stays an indicator at 3:1 on the panes, where it marks the selected row.
- An input's edge is the only thing that shows where the box is (its fill is within 1.1:1 of the card), so the edges of fields, unchecked check boxes and radio buttons, and the off toggle's track and thumb are held to 3:1 against `bg.base`, `bg.surface` and `bg.raised` (WCAG 1.4.11): fields use `border.control` (light 3.98:1 on `bg.base`, 4.30:1 on `bg.surface`, 4.00:1 on `bg.raised`; dark 3.96:1, 3.70:1 and 3.34:1), check boxes and radios `text.muted`, the toggle `text.secondary`. *(v0.5.4)*
- In high contrast every token is a system colour: the tints are the window colour, and hover, selection and a banner's kind are shown by the border and the words. The frame is the window with the window text on it; the band and fold are drawn in the window colour, so they are not seen, and the selected tab keeps its Highlight outline.

Other rules: one accent only. State colors never appear as decoration.

### 2.2 Typography

| Token | Value |
|---|---|
| `font.ui` | "Segoe UI Variable Text", "Segoe UI", Inter, sans-serif *(v0.5: the Windows 11 face first)* |
| `font.display` | "Segoe UI Variable Display", "Segoe UI", Inter, sans-serif: screen titles *(v0.5)* |
| `font.mono` | "Cascadia Mono", Consolas, "JetBrains Mono", monospace |
| `size.xs` | 11 px: timestamps, badges |
| `size.sm` | 12 px: table cells, meta, hints |
| `size.md` | 14 px: **base body** *(v0.5, was 13: the Windows 11 body size)* |
| `size.lg` | 16 px: section headings |
| `size.xl` | 24 px: screen titles |
| `weight.regular / medium / semibold` | 400 / 500 / 600 |
| `lineheight` | 1.43 body, 1.25 headings |

The brand wordmark's Exo 2 is not an app font (§2.0).

### 2.3 Spacing, radius, elevation, motion

- Spacing scale: `4, 8, 12, 16, 24, 32, 40` px. Pane padding 24; card padding 20; control padding 5×14 inside a 32 px control; list and table row height 36; readable content width 760. *(v0.6.1)* The envelope's label column 52 (`layout.envelope-label`), a narrow field such as Time's minutes 72 (`layout.field-narrow`), the receipt's side column 300 (`layout.side-column`), and the least height Review's note and screenshots panes get, 300 (`layout.pane-min-height`): below it the Review form scrolls as a whole rather than squeezing the panes under the envelope or an open timeline. The inbox rail's width, 280 (`layout.rail-width`, v0.6.4), which leaves the session pane 744 at the 1024 minimum.
- Radius: `6` controls, `8` cards, `999` pills.
- Elevation: layers, not shadows. The window is `bg.base`, panes and cards are `bg.surface` with a `border.subtle` outline, controls are `bg.raised`. The frame (rail, title bar, pill) is `shell.bg`, a flat fill with no shadow of its own. Only the HUD and toasts float, with `0 8px 24px` at 40% black in dark and 14% in light.
- Motion: `120 ms` state changes, `180 ms` pane open/close, `ease-out`. No bounce, no parallax. Respect the "reduce motion" OS setting.

### 2.4 Iconography

Lucide icon set, 16 px in controls, 20 px in navigation. The tray and the app icon use the brand files in `shared/design/brand/` (§2.0, tray glyph tiers): idle in Iris or Ribbon 400 by taskbar, recording with a red dot, paused with two bars, offline grey with a cross, draft ready with a count badge. State glyphs in the app: recording a filled dot, paused two bars, out of scope eye-off, published a check, offline a cloud with a slash, danger a cross; each always beside its word.

---

## 3. Component library (WPF; web mirrors)

| Component | Variants | Notes |
|---|---|---|
| Button | primary, secondary, ghost, danger | 32 px tall; icon + label; primary is the one filled Iris button on a screen; disabled shows its reason beside it |
| Input / Search | default, error | inline validation message below, never a dialog |
| Toggle | default, locked (admin) | locked shows lock icon + "Set by your admin" tooltip |
| Select / Combo | searchable | used for ticket picker, mic device |
| Table | virtualized, sortable | 32 px rows, sticky header |
| Badge | state, confidence, suggested | pill, 11 px, uppercase off; the word in `text.primary`, the hue in its icon and border |
| Toast | info, success, warning, error | bottom-right, 6 s, action button optional |
| Banner | info, warning | inline at top of a pane; dismissible when informational; info is `bg.selected` with an `accent.primary` icon |
| Modal | confirm, destructive | only outside recording; destructive requires typed confirmation for irreversible data loss |
| Split pane | 3-pane, resizable | persists sizes |
| Filmstrip | thumbnails 160×90 | selection, exclude, blur, delete |
| Timeline | horizontal scrubber | markers for clicks, frames, narration, suppression, scope |
| Status pill (HUD) | recording/paused/suppressed/scope/offline | see §5 S2 |
| Status chip (shell header) | recording/paused/idle/offline | the capture state in words, "Recording: ScreenConnect (12:41)", with the `StateDot` and a border in the state's colour; the words stay `text.primary` (on the frame, `shell.text` and the `shell.*` state colour) *(v0.5.5)* |
| Frame *(target, v0.6.0; rail built v0.6.4)* | rail, title bar, pill | `shell.bg` with `shell.text`; the title bar is tinted with `DWMWA_CAPTION_COLOR` and carries the small glyph; no blur or Mica |
| Inbox row *(target, v0.6.0; built v0.6.4)* | to check, not confirmed, published | company and age, then the title, then ticket, tool and duration; the selected row joins the note like a tab, with the 4 px `brand.ribbon.band` over it. As built, the row has the tool and age, then duration and screenshots, since History's rows carry no company, ticket or title (§5 S4, as built) |
| Envelope *(built v0.6.1)* | draft, publishing | four labelled lines (To, As, Time, Also) above the note, with Publish as the screen's one filled button (§5 S3) |
| Receipt *(built v0.6.1)* | sent, partial, not confirmed, failed | the Review pane after publish: one row per destination and Open next (§5 S3) |
| Save bar *(target, v0.6.0)* | hidden, pending, saving, failed | one per Settings page, pinned, never scrolls; names each change (§5 S5) |

Focus ring: 2 px `accent.primary` outside the control (`shell.accent` on the frame); visible for keyboard focus only.

---

## 4. Global behaviors

- **Keyboard first.** Every action reachable without mouse; shortcuts listed per screen; `?` opens a shortcut sheet.
- **Never interrupt recording.** No modals, no focus-stealing while a session is recording. Warnings go to the HUD.
- **Undo over confirm** for reversible actions (exclude frame, reorder step). Typed confirmation only for irreversible deletes.
- **Errors say what happened and what to do.** Pattern: `<What happened>. <What to do>.` Example: "ConnectWise rejected the request: clientId header missing. Add your clientId in Settings → Integrations."
- **Time** shown relative in lists ("14 min ago") with absolute on hover.
- **Empty states** always contain one primary action.
- **Copy voice:** plain, present tense, no exclamation marks, no "Oops". Use "note" not "documentation", "session" not "recording" in tech-facing copy, "capture" for the act. The publish action is always "Publish", never "Send": a published note cannot be recalled.

---

## 5. Screens

### S1: Tray icon and quick menu

**States:** idle · recording (red dot, tooltip "ScreenTail · Recording: ScreenConnect (12:41)") · paused (two bars) · draft ready (badge count) · offline (grey with a cross) · service stopped (warning glyph). Icons per §2.0, tray glyph tiers.

**Menu (top to bottom):**
```
● Recording: ScreenConnect (12:41)       [state line, non-interactive]
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

A remote tool is named the way the registry's `display_name` names it (ScreenConnect, Remote Desktop, AnyDesk), never by its id (`screenconnect`, `rdp`), here, in the shell's status chip and in History *(v0.5.5)*.

**A publish not confirmed:** between sessions (idle, draft ready, draft failed, drafting) the state line ends with how many sessions hold a publish that may have reached its ticket: "Not capturing · 1 publish not confirmed", "Draft ready · 2 publishes not confirmed". The tooltip says the same when the whole of it fits in the 63 characters Windows shows, and otherwise keeps to the state. Never while a session is being captured: the line is then about the capture alone. The icon does not change. *(v0.5.3)*

**Discard session… while recording:** choosing it stops capture first (the session moves to `finalizing`), then shows the typed-confirmation dialog ("DISCARD"). No modal ever appears while a session is recording. *(v0.4.1, Q3)*

**"What's being captured right now?" panel** (small window, 360×420): active window name and scope status; mic device + live level; suppression state; redaction backlog count; Local-only badge; policy version; CPU/RAM; a "Copy diagnostics" button (no content, only states).

### S2: Recording HUD (the pill)

Draggable pill, `999` radius, 44 px tall, docked by default to the top-right edge, elevated. Always on top, does not take focus, excluded from capture.

**Target (v0.6.0).** The pill is part of the frame: `shell.bg` with `shell.text`, a `shell.muted` outline, and a 4 px `brand.ribbon.band` cap on its leading edge (decorative, §2.1). Pause, Stop and Mark are always one click, each a 32 px control with its icon and, on hover or focus, its name and hotkey ("Pause  Ctrl+Alt+P").

```
┌──────────────────────────────────────────────────────────────┐
▌ ● Recording 12:41   ⛨ 2   [ ‖ Pause ] [ ■ Stop ] [ ⚑ Mark ]  │
└──────────────────────────────────────────────────────────────┘
```

Clicking the pill's body expands it to the session's tool and start ("ScreenConnect session, since 14:02"), the last three events as types and times only ("Screenshot taken 14:14:03", "1 item masked 14:13:51", "Voice, 14 seconds 14:13:20"), Mark moment and Open in ScreenTail. The pill never shows screen text, transcript, company or ticket, so nothing sensitive can appear over a customer's desktop (INV-10). On the frame, states use the `shell.*` colours, each with its word and icon.

**v0.5 layout (current build until the pill PR):** `bg.surface` with `border.strong`; `● 12:41   ▂▄▆ mic   ⛨ 3 redacted   [ ‖ Pause ] [ ■ Stop ]`.

**State variants (left segment), both layouts:**
- Recording: red dot, "Recording" and the timer.
- Paused: amber `‖ Paused: press Ctrl+Alt+P to resume`.
- Suppressed: amber `⏸ Paused: sensitive field` / `Paused: excluded app` / `Paused: elevated window`, auto-resumes. The elevated-window tooltip reads "Elevated window: screen not captured." *(v0.4.1, Q5)*
- Out of scope: slate teal eye-off and `Not capturing: Outlook` (clicks logged, no frames). The process name and nothing more (INV-10), spelled the way its product is: `OUTLOOK` reads "Outlook", and a process this version does not know is title-cased *(v0.5.5; colour v0.6.0, was violet)*.
- Mic missing: grey mic glyph and "No mic", tooltip "No microphone. Capture continues without voice."
- Offline: grey cloud glyph and "Offline"; tooltip "Offline: draft will be created when connected."

**Interactions:** click pill → expand to show the last 3 timeline events and "Mark moment" (Ctrl+Alt+M). Double-click → open Review of current session (read-only until stopped). Right-click → hide HUD for this session (tray remains). Position persists.

**Screen sharing:** the HUD stays visible to the technician at all times, including while they share their screen with a customer. `WDA_EXCLUDEFROMCAPTURE` keeps it out of screen-share and recording apps, so viewers never see it (verified in ST-001 AC3). There is no auto-hide, because that would create a silent-capture path (INV-4). *(v0.4.1, Q1; still holds in v0.6.0)*

### S3: Review & Publish (the product)

Window default 1280×800, minimum 1024×680.

**Target (v0.6.0): Outbox.** Two panes inside the frame: the inbox rail on the left (S4) and the session on the right, an envelope header above the note as it will land.

```
┌ Frame: ◆ ScreenTail          ● Recording 12:41 [Stop]                                              ┐
├─────────────────────────────┬────────────────────────────────────────────────────────────────────┤
│ [Company, ticket or title ] │ Printer offline: print spooler stopped           [Discard] [Publish] │
│                             │                                                         Ctrl+Enter │
│ To check  2                 │ To    #48213 Printer offline · Acme Dental            Change Ctrl+T │
│▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔│       Matched from the ScreenConnect window title                  │
│ Acme Dental      14 min ago │ As    (•) Internal note  ( ) Discussion: the customer sees it      │
│ Printer offline: print spo… │ Time  0:15  12 min, rounded up to 15                               │
│ #48213 · ScreenConnect · 12 │ Also  [x] Time entry  [x] KB article in Hudu: "Restart a stopped…" │
│ Northwind Legal    2 h ago  │ 1 inferred step not checked                                        │
│ Outlook keeps asking for a… │ ─────────────────────────────────────────────────────────────────  │
│ No ticket yet · Remote Desk │ This is the note as it will read on the ticket. Click any line…    │
│                             │ Problem                                                            │
│ Not confirmed  1            │ Nothing would print from the reception workstation; …              │
│ Acme Dental      Yesterday  │ Steps                                                              │
│ VPN drops every 20 minutes  │ 1  Found the Print Spooler service stopped.   14:04   ┌──────────┐  │
│ ? Not confirmed · #48166    │    "Spooler service is stopped, that's why…"          │ frame    │  │
│                             │ 2  Started the service and set it to restart…  14:06  └──────────┘  │
│ Published today  2          │ 3  ⚠ Cleared the stuck queue and printed a test page.              │
│ ✓ Birchwood Vets   #48190   │    Inferred from screen only   [Looks right Alt+C] [Edit]          │
│ Earlier  31                 │ Result · Follow-ups                                                │
│ Settings · Diagnostics      │ 2 items masked, none left to review · Saved                        │
└─────────────────────────────┴────────────────────────────────────────────────────────────────────┘
```

- **Envelope header.** Four labelled lines, each a value read in one pass.
  - **To:** the bound ticket as a value, "#48213 Printer offline · Acme Dental", with why it was matched ("Matched from the ScreenConnect window title") and Change (`Ctrl+T`), which opens the ticket picker in place. With no ticket, To reads "No ticket yet" with Choose a ticket, and Publish is disabled with its reason beside it.
  - **As:** a visible two-way choice, Internal note (default, v0.4.1 Q2) or "Discussion: the customer sees it". Never hidden behind Options.
  - **Time:** the time entry and its rounding ("0:15, 12 min, rounded up to 15"), editable in place.
  - **Also:** the other destinations as checkboxes, "Time entry" and "KB article in Hudu: <title>", the KB default following `kb_candidate` with its reason.
  - Beside Publish, what is still open ("1 inferred step not checked") and, when disabled, the reason (v0.4.3).
- **Publish is the one filled button** on the screen (Iris, `text.on-accent`), `Ctrl+Enter`. Discard stays ghost.
- **The note is edited in place as it will land.** Problem, Steps, Result, Follow-ups in ConnectWise's plain-text form, so there is no preview to drift out of sync. Each step row carries its own frame and quote (the screenshot column and the "frame 2" chips go). An inferred step shows `confidence.low` ⚠, "Inferred from screen only", Looks right (`Alt+C`, or `Enter` on the focused step) and Edit. A masked part of a frame stays masked. Under the note: the masking count and the save state.
- **The receipt.** After publish, the session pane becomes a receipt (a state of the same pane, never a dialog): "Published to ticket #48213", the company, title and time; one row per destination with its badge (Sent, Failed, Not confirmed, Already published) and its provider-named link ("Open in ConnectWise", "Open in Hudu"); the session's facts (minutes captured, steps checked, screenshots, items masked, time from open to published); the line "Need to change something? Edit the note in ConnectWise. Publishing again from here would add a second note."; and **Next to check** with the next session in the inbox and **Open next** (`Enter`). A destination that did not confirm shows "Not confirmed" with the "I checked ticket #48213" box in this same list (v0.5.2). Calm: no confetti (principle 5). The receipt rule is `brand.ribbon.band`.
- **Timeline and transcript** stay a collapsible bottom panel (`Alt+T`), as below.
- **As built (v0.6.1).** Until the inbox rail and the note-as-it-lands PRs, the envelope is a card above the v0.5 note and screenshot panes, and the receipt takes the place of all three and the timeline. The rules the build settled:
  - **Subject line** is the note's own title, without "Review:". Discard (ghost, GhostDanger) then Publish (filled, send glyph, "Ctrl+Enter" inside the button). Under them, right-aligned: the reason Publish waits (amber warning glyph, a polite live region, also the button's help text) and what is still open ("1 inferred step not checked", `confidence.low` glyph). With no PSA, Publish is replaced by the secondary button "Connect a PSA to publish", which opens Settings, and the line under it reads "No PSA connected. Nothing can be published until one is connected in Settings, under Integrations."
  - **To:** the ticket chip ("#48213" semibold, then "Printer offline · Acme Dental"), "Matched from the <tool> window title" only for a suggested ticket, and Change or Choose a ticket with its "Ctrl+T" key cap. The picker opens under the value with focus in the search; the arrows move through the matches, `Enter` or a click takes one and closes it, `Esc` closes it and focus returns to Change.
  - **As:** a segmented control, "Internal note" (lock glyph) and "Discussion: the customer sees it"; the chosen one is the selection tint, semibold, with a 2 px accent bar, so high contrast keeps the bar and the weight.
  - **Time:** stays whole minutes ("15" then "min"), not "0:15", so the T-386 check on what is typed is unchanged; the note reads "12 min, rounded up to 15" (or "15 min" when nothing was rounded).
  - **Also:** a chip for each destination, the ticket note first (it can be unticked after an earlier publish, O3), then "Time entry" and "KB article in Hudu: “<title>”"; the article's `kb_reason` is a muted line under the chips. The O3 "I checked ticket #N" warning sits under the envelope.
  - **Receipt heading** sits on a frame plate (`shell.bg`, `shell.text`) ruled with a 4 px `brand.ribbon.band`, with a glyph in a ring in the `shell.*` state colour: Sent "Published to ticket #N" (check, green), Partial "Partly published to ticket #N" (warning, amber), Not confirmed "Not confirmed on ticket #N" (question, amber), Failed "Not published to ticket #N" (cross, red). The subline is company, ticket summary and the time it landed. The §6 summary line shows when the heading does not say it all.
  - **Rows** add what went there beside the destination ("Internal note", "0:15", the article's title). Retry and "Map and publish the article" are secondary buttons on the receipt, so Open next is its one filled button; the mapping prompt and the not-confirmed check each lead with a glyph and a word.
  - **Facts:** minutes captured (the session's duration), steps checked ("2 of 3": high confidence or confirmed), screenshots and items masked in what was sent, and open to published (opening Review to the first landing, in seconds under a minute).
  - **Next to check** reads History's rows, which carry a tool, a duration and a start and never a company or title (the ST-079 list contract; corrected in v0.6.4, which first wrote INV-10 here, though INV-10 is about logs and metrics), so the card reads "Remote Desktop session" and "9 min · started 13:00" (and "publish not confirmed" when that is why it waits). Order: To check (drafts) newest first, then Not confirmed. Open next has focus when the receipt appears; `Enter` on a check box does not open it. With none left: "Nothing else to check right now." **Today** counts Published (the session just published included) and Left to check; time logged waits for the rows to carry it.
  - **Mapping needed** (ST-097) is a receipt state, since it is known only after the publish answers.

**v0.5 layout (current build until the Publish/envelope and workspace PRs).** Three resizable panes plus a collapsible bottom panel.

```
┌ Title bar: "Review: Printer offline · Acme Dental · 12 min · Sep 10, 14:02"     [Discard] [Publish ▸] ┐
├──────────────────────────────┬──────────────────────────────┬──────────────────────────────────────┤
│ NOTE (editable)              │ SCREENSHOTS (7 of 14)        │ PUBLISH                              │
│                              │                              │                                      │
│ Problem                      │ ┌────┐ ┌────┐ ┌────┐ ┌────┐  │ Ticket                               │
│ [text…]                      │ │ 1  │ │ 2  │ │ 3  │ │ 4  │  │ [🔍 #48213 Printer offline: Acme   ] │
│                              │ └────┘ └────┘ └────┘ └────┘  │  Suggested from window title  ✓      │
│ Steps                        │  ✓      ✓      ✓      –      │                                      │
│ 1. Checked spooler service,  │ ┌────┐ ┌────┐ ┌────┐         │ Note type   (•) Internal ( ) Discussion│
│    stopped [frame 2]         │ │ 5  │ │ 6  │ │ 7  │         │ Time entry  [ 0:30 ] ▾  (rounded 15) │
│ 2. ⚠ Restarted spooler       │ └────┘ └────┘ └────┘         │                                      │
│    (inferred from screen)    │                              │ Destinations                         │
│ 3. Cleared print queue       │ [Include] [Blur…] [Delete]   │ [x] Ticket note                      │
│    "clearing the queue now"  │                              │ [ ] KB article  (not a KB candidate) │
│ ⋮ drag to reorder            │                              │                                      │
│                              │                              │ ───────────────────────────────────  │
│ Result                       │                              │ [        Publish   Ctrl+Enter      ] │
│ [text…]                      │                              │                                      │
│                              │                              │ After publish: links to note, time   │
│ Follow-ups                   │                              │ entry, article: "Open in ConnectWise",│
│                              │                              │ "Open in Hudu"                        │
│ [text…]                      │                              │                                      │
├──────────────────────────────┴──────────────────────────────┴──────────────────────────────────────┤
│ ▸ Timeline & transcript  ─●──●───●●─────●──[▮▮ suppressed]──●──   14:02:10  "clearing the queue now" │
└──────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Note editor (both layouts)**
- Sections fixed in order: Problem, Steps, Result, Follow-ups. Section headings are `size.lg` semibold, `text.primary` (v0.5.0).
- Steps are a list; each item: text (inline editable), its evidence (v0.5: `frame_refs` chips, "frame 2", that highlight the filmstrip on hover and jump on click; target: the frame on the step's row), an optional quoted transcript snippet in `font.mono` `text.muted`.
- Low-confidence steps show a `confidence.low` ⚠ marker with tooltip "Inferred from screen only. Please verify." Marker clears when the step is edited or explicitly confirmed (`Alt+C`).
- Drag handle on hover; `Alt+↑/↓` reorders. `Enter` at end of a step creates a new step; `Backspace` on empty deletes.
- Autosave indicator bottom-left of pane: "Saved" / "Saving…" / "Not saved: retrying" in `text.muted`.
- Banners at top of pane when applicable: *Partial capture* ("Capture started late or was interrupted. Some steps may be missing."), *Frames purged* ("3 screenshots were removed because they could not be redacted in time."), *Local draft* ("Drafted on this device only. Quality may be lower.").

**Screenshots (v0.5 centre pane; target: on each step's row, same actions)**
- Filmstrip grid, 160×90 thumbnails, ordered by time. Included frames have a check; excluded are dimmed. Suppressed/out-of-scope intervals appear as slim striped gaps with a tooltip.
- Toolbar: Include/Exclude (`Space`), Blur… (`B`, opens region-draw overlay on the enlarged frame; destructive, with one-step undo until pane loses focus; from the keyboard, `B` puts a box in the middle of the frame with focus on it: arrows move it, `Shift`+arrows resize it, `Ctrl` takes bigger steps, `Enter` blurs what it covers, `Esc` puts it away *(v0.5.1)*), Delete (`Del`, typed confirmation not required but a 5 s undo toast is shown; deletion becomes permanent after), Enlarge (`Enter`).
- Header shows "7 of 14 included". Enlarged view shows cursor marker and OCR text toggle (`T`) for verification.

**Publish (v0.5 right pane; target: the envelope header and the receipt)**
- Ticket picker: searchable combo, shows `#id · summary · company`; "Suggested" badge when inferred; recent tickets listed on focus.
- Note type radio, default **Internal**: Discussion notes are customer-visible in ConnectWise, and a first draft shouldn't default to customer-facing. Tenants can change the default in Settings → Integrations. *(v0.4.1, Q2)* Time entry field with rounding note; editable.
- Destinations: checkboxes; KB toggle default follows `kb_candidate` with reason text.
- Primary **Publish** button; when disabled, its reason is written beside it ("Choose a ticket first").
- After publish: the pane switches to a result list, one row per destination, and the title bar gains a `Published` badge. Each row's glyph is drawn in its state's colour (✓ `state.published`, ✗ `state.danger`, ? `state.paused`) beside a badge in words, "Sent", "Failed", "Not confirmed" or "Already published", and the glyph keeps its spoken name. Each link is named for the provider its destination went to, from what is connected: "Open in ConnectWise" for the note and time entry, "Open in Hudu" for the KB article. Retry on any failed destination says where it sends again: "Retry Hudu", read as "Retry Hudu: the KB article", the label first in its name (WCAG 2.5.3). *(v0.5.5)*
- An article whose company is not mapped (ST-097) says why once, in the prompt that holds the choice ("“Acme Dental” is not mapped to a company in Hudu yet. Choose the one it is and the article goes there. The choice is remembered."); the summary only points at it ("Note published. KB article not sent yet: choose its company below") and the row says Failed without the reason. *(v0.5.5)*
- A destination that may have landed (the PSA did not confirm, the publish ran out of time, or the connection to the capture service dropped before it answered) is marked `?` (read by a screen reader as "Not confirmed") and called "not confirmed", never "failed"; one the service refused because the session is already published says "Already published: check the ticket before publishing again". A warning above Retry says "This may already be on the ticket. Check the ticket, then confirm below before publishing again, so nothing is added twice", with a checkbox "I checked ticket #48213 and want to publish again". Publish, `Ctrl+Enter` and Retry stay disabled until it is ticked; it is in the tab order, the warning is a polite live region that stays up once the box is ticked, the box is disabled while a send is on its way, and the tick is spent on one send. *(v0.5.2)*
- Reopening Review for a session an earlier publish reached, or may have reached, or after the service refuses one: the form comes back with each destination as that publish left it. "Ticket note  (already on the ticket)", "KB article  (already in the knowledge base)" and "? Ticket note  (not confirmed)" (read as "Ticket note, not confirmed") start unticked; the rest are as for a first publish (note and time entry ticked, the article as the draft suggests). A warning above Publish holds the same "I checked ticket #48213 and want to publish again" box, which only a ticked landed or not-confirmed destination needs; after the service's refusal, the warning starts with the refusal's words. *(v0.5.2, owner decision O3)*

**Bottom panel: Timeline & transcript**
- Collapsed by default to a 40 px scrubber; `Alt+T` toggles. Expanded: 220 px with monospace transcript lines `[14:02:10] clearing the queue now`; clicking a line highlights its frame; markers for clicks (dots), frames (squares), suppression (striped), scope (slate teal band, v0.6.0), "mark moment" (flag).

**Header actions:** `Discard` (ghost, danger on hover, then typed confirmation "DISCARD"), `Publish` mirrors the envelope's (v0.5: the right pane's) button. While any session is being captured Discard asks nothing (§4). While its own session is being captured, Discard is disabled and "Stop the session to discard" shows beside it, a polite live region and the button's help text, as Publish's reason does (v0.4.3); it comes back when capture stops *(v0.5.5)*. While another session is being captured, pressing Discard asks nothing and the line beside it says "A session is being captured. Stop it first, then discard this one."; if one starts while DISCARD is typed, nothing is discarded and the line says the same.

**Shortcuts:** `Ctrl+Enter` Publish · `Ctrl+T` change ticket *(v0.6.1)* · `Enter` on the receipt, Open next *(v0.6.1)* · `Ctrl+S` save now · `Alt+1/2/3` focus the note, the screenshots, the envelope (the receipt once published) · `Alt+T` timeline · `Alt+C` confirm step · `Alt+↑/↓` reorder · `Space` include/exclude · `B` blur · arrows, `Shift`+arrows, `Ctrl`, `Enter`, `Esc` place, size, apply or put away the blur box · `Del` delete frame · `?` shortcut sheet · `Esc` close enlarged frame (put the blur box away first, when it is out).

**Empty/edge states:** draft failed → note pane shows "We couldn't draft this session" with Retry and the timeline/transcript still available, and Publish stays disabled with the reason "There is no note to publish yet. Retry the draft first." (writing a note by hand after a failed draft is v1.1) *(v0.4.1, Q4)*; offline → "Draft pending: offline" banner and disabled Publish with the reason "There is no note to publish yet. It will be drafted when you are back online." *(v0.4.3)*; no integrations → Publish replaced by "Connect a PSA to publish" linking to Settings; no session to review → "No draft to review yet. Record a session and stop it with Ctrl+Alt+S.", or while one is being captured "Capturing now: the draft opens here when you stop (Ctrl+Alt+S)." *(v0.5.5)*.

### S4: Session history

**Target (v0.6.0): the inbox rail.** History becomes the left rail of the workspace (S3), part of the frame (`shell.bg`), so opening a session is one click.
- Search at the top over company, ticket and title ("Company, ticket or title", `Ctrl+F`).
- Groups, in order, each with its count: **To check** (drafts waiting), **Not confirmed** (a publish that may have landed), **Published today**, **Earlier** (collapsed). A not-confirmed session has its own group, so settling one is a few actions.
- Each row: company and age ("14 min ago"), then the title, then ticket, tool (S1 name) and duration ("#48213 · ScreenConnect · 12 min", or "No ticket yet"). A state badge where the group does not already say it (Not confirmed, Published).
- The selected row joins the session pane like a tab, with the 4 px `brand.ribbon.band` over it; in high contrast the tab keeps its Highlight outline.
- Settings and Diagnostics sit at the foot of the rail.
- Everything in the v0.5 History below (filters, coverage banner, bulk discard, Mark as published, export, empty state, the no-dialog-while-capturing rules) keeps its behaviour; the screen PR decides where each sits in the rail or the session pane.
- **As built (v0.6.4).** The frame and the rail are built round the session pane, which keeps S3's v0.6.1 envelope and receipt over the v0.5 note and screenshot panes until the note-as-it-lands PR. The rules the build settled:
  - **Frame.** The rail is `shell.bg`; the session pane is `bg.base`. There is no title band inside the window: the window's own title bar already says "ScreenTail", and a 48 px band under it cost the session pane the height Review needs at the 1024×680 minimum (beside the rail its envelope wraps, and with the band the form scrolled and the note's and screenshots' scroll bars went under the window's edge). The status chip heads the rail instead, in the frame's colours (`shell.text`, with the `shell.*` state colour on its dot and edge, §3), and its words wrap in the rail's width. The v0.5 header tabs go: the rail is History, the pane is Review, Settings is in the rail's foot. The rail is 280 px (`layout.rail-width`). Not yet built: the title bar tinted with `DWMWA_CAPTION_COLOR`, and the small glyph, which waits for the asset PR.
  - **Rows carry no company, ticket or title (deviation).** INV-10 forbids content in logs and metrics; it does not forbid showing a title on screen, and this build does not read it as doing so. The rows cannot carry them for other reasons: they come from `list_sessions`, whose rows (ST-079) are a status, a tool, a start, a duration, screenshot counts and the not-confirmed mark, never note text; and the company is not kept on the device at all (it comes from the PSA when a ticket is looked up). The store keeps the note's title in the draft and the ticket number read off the window (`suggested_ticket`, digits only), and the list carries neither. So each row reads "<Tool> session" and its age ("14 min ago", "2 h ago", "Yesterday", then "Mon 14 Sep"), then duration and screenshots ("12 min · 7 screenshots"), with a state badge where the group does not say it: Partial capture, No draft yet, Published (in Earlier) and Discarded, each a word with a glyph in the state's colour. A screen reader hears the row with its start ("ScreenConnect session, 1 h ago, started Wed 16 Sep 09:00, 1 min · 7 screenshots"), so no two rows read the same. Adding the title and ticket number to the list is a contract change for the owner (amendment v0.6.4).
  - **Search** (`Ctrl+F` from anywhere in the window, `Esc` in the box clears it) looks through what the rows carry: the tool, the group and state words, the day ("today", "yesterday", the weekday, the date) and the start time, and every word typed must match. The hint reads "Search by tool, day or state" in place of "Company, ticket or title". A search that matches nothing says "No sessions match “anydesk”." with Clear search. Typing a search opens Earlier, so a match there is not hidden.
  - **Groups.** To check holds drafts, partial captures and sessions with no draft yet (still being captured, being drafted, or a failed draft), newest first. Not confirmed holds every session whose publish may have landed, whatever its status, but never a discarded one. Published today goes by the day the session started, since the rows carry no time of publishing (a session started before midnight and published after it is Earlier). Earlier holds older published sessions and every discarded one, starts collapsed behind a toggle named with its count ("Earlier, 31 sessions"), and opens by itself when it holds the open session. A group with no rows is not shown. Each group is a list named with its count ("To check, 2 sessions"); Tab lands on its open row, or its first, and the arrows move within it. The list is read again when the service answers, capture changes state or session, a session is opened or the view changes; the rows are updated in place, so focus stays on the row it was on.
  - **Opening.** Selecting a row, by a click or with the arrows, opens its session in the pane, as a History row's Open did. The open row is the pane's tab: `bg.base` and the content tokens, the 4 px `brand.ribbon.band` along its top, and a 1 px `shell.accent` edge, which high contrast draws in Highlight where the band is the window colour. With Settings or All sessions in the pane, no row is the tab and the foot marks the view instead (`shell.hover` and a 3 px `shell.accent` bar).
  - **Keyboard focus on the frame** is a 3 px `shell.accent` ring 3 px outside the control, as on the pill (v0.6.2), since `accent.primary` is 2.80:1 on the light frame.
  - **Foot:** All sessions, Settings, Diagnostics. Diagnostics opens the panel the tray's "What's being captured?" opens (ST-071).
  - **Where the v0.5 behaviour sits.**
    - Bulk discard with the typed count, Mark as published, the selection note and the line beside the buttons stay in the v0.5 table, now titled **All sessions** and opened from the rail's foot (deviation: the foot gains a third item). A rail where one click opens a session is not also a multi-select list, and the table already holds those rules and their tests. While any session is being captured, Bulk discard and Mark as published ask nothing and say why beside the buttons, as before.
    - Review's Discard still goes to All sessions afterwards, where the list says what happened.
    - The empty state's sentence ("No sessions yet. … press Ctrl+Alt+R to start one now.") is the rail's body when there are no sessions; the pane says what Review says with nothing to show.
    - Export audit log stays in Settings, under Privacy, where ST-081 built it; it was never a History button in the build.
    - The date, status and tool filters and the coverage banner were never built in the v0.5 screen (their rules are in Core, `SessionHistory.Filter` and `CoverageOf`, with no caller). The rail's search and groups do the filters' job; the coverage banner is left for a later PR (owner decision: on the rail or on All sessions).
  - **Shortcuts:** `Ctrl+F` search the rail (also on the shortcut sheet), `Esc` clear the search.

**v0.5 (current build until the workspace PR).** Table: Status (badge) · Started · Duration · Tool · Client/ticket (if published) · Frames · Actions. Filters: date range, status (Draft, Published, Discarded, Partial, Pending), tool. Coverage banner: "Captured 46 of 52 remote sessions this week (88%)". Row click opens Review. Multi-select → Bulk discard (typed confirmation: type the number of selected sessions, e.g. `3`) *(v0.4.1, Q6)*. While any session is being captured nothing is asked (§4), whichever sessions are selected, and the line beside the buttons says "A session is being captured. Stop it first, then discard the selected sessions."; if one starts while the count is typed, nothing is discarded and the line says the same. "Export audit log" button (JSON/CSV; no content). The Tool column shows the tool's name (S1), not its id. Empty state: "No sessions yet. Start a remote session and ScreenTail will capture it, or press Ctrl+Alt+R to start one now." The UI has no command that starts capture (the service registers the hotkey and starts the session itself), so the hotkey is the next step in place of a Start capture button *(v0.5.5)*.

**Not confirmed** *(v0.5.3)*: a session whose publish may have reached the ticket (S3's "not confirmed") shows a second badge, "Not confirmed", beside its status badge: warning fill (`bg.warning`) with the `state.paused` border, so it reads as neither Published (green) nor failed (danger). It is not a status and not a filter value: the session is still a Draft that can be discarded, or Published in part. A screen reader hears it in the row's name ("Draft, publish not confirmed, started ..."); the badge is not a tab stop. A discarded session never shows it. With exactly one such row selected, **Mark as published** (secondary button, left of Bulk discard) settles it for a technician who checked the ticket and found the note there. It asks first, with Cancel as the default, naming the session in the words on its row (when it started, how long, the tool): "Mark the session started Tue 10 Nov 14:02, 9:00, ScreenConnect as published? Only do this after checking the ticket. ScreenTail will record that what the earlier publish sent is on the ticket, and will not send it again. A published session is kept and can no longer be discarded." Nothing is sent to the ticket. If a session started being captured while the question was up, the answer is not acted on and the line beside the buttons says why. Afterwards the row reads Published without the second badge and the line beside the buttons says "Marked as published."; a refusal is said there in the service's words, never in a dialog. While a session is being captured nothing is asked (§4) and the line says "A session is being captured. Stop it first, then mark this one as published."

### Settings (S5 to S7): layout

**Target (v0.6.0, owner decision): a left nav and one Save bar for the whole page.**
- **Left nav** inside the frame: Capture, Hotkeys, Privacy and masking, Integrations, This device (with "Activated with <tenant>"). A section with unsaved changes shows their count beside its name.
- **One Save bar** for the whole Settings page, not one per section. It appears only when something changed, is pinned to the bottom, and never scrolls away. It names each change as a link that jumps to it, across sections ("2 changes not saved: What to capture, Retention (Privacy)"), with Undo changes and **Save** (`Ctrl+S`), the bar's one filled button. A failed save says why in the bar, in the `<What happened>. <What to do>.` pattern.
- **Changed** marks each changed setting beside its label until saved.
- **Leaving Settings keeps the changes pending, with no dialog.** Test connection on an integration checks the keys without saving them.
- A setting locked by policy shows its lock and "Set by your admin", and is never a pending change (INV-11).
- Until the Settings PR lands, the build keeps its v0.5 layout.

### S5: Settings → Capture

Sections: *Remote tools* (list with toggles per tool and browser pattern; "Add custom…"); *Capture scope* (radio: Remote-tool windows + admin tools (recommended) / All windows during a session; selecting "All" shows an inline warning about other customers' data); *Auto-start* toggle and grace period slider (30 to 300 s); *Hotkeys* (editable chords with conflict detection; target: its own nav entry); *Screenshots* (scene-change sensitivity Low/Med/High); *Microphone* (device combo, live level meter, model size Small/Medium with size and accuracy hint); *Start UI at login* toggle.

### S6: Settings → Privacy & Redaction

Sections: *What leaves this device* (a short data-flow diagram in plain language: captured on device → redacted on device → summary request (or local model) → you publish); *Local-only mode* toggle (locked if policy) with quality note; *Excluded apps and windows* (list, add process/title/URL; defaults labeled "Recommended"); *Redaction patterns* (toggles: SSN, cards, API keys, passwords; custom regex with live validator and test box); *Retention* (slider 1 to 30 days; shows what is deleted vs kept); *Usage telemetry* (toggle with the exact list of fields sent); *Your data* (Export audit log · Delete everything, typed "DELETE"). Your data's actions act at once and are never part of the Save bar. While a session is being captured, Delete everything asks nothing (§4): the line under the buttons says "A session is being captured. Stop it first, then delete everything.", and the same if one starts while the phrase is typed. Its other refusals are said there too, never in a message box.

### S7: Settings → Integrations

Cards per integration: **ConnectWise Manage** (site/region, company ID, public key, private key, clientId; Test connection → shows member and company; default note type; time rounding 6/15; optional per-technician API member), **Hudu** (base URL, API key; Test connection; central vs company-scoped KB), **Company mapping** (table CW company ↔ Hudu company, auto-matched with confidence badge, editable). Secrets masked after save; "Change" reveals fields. Test connection never saves (Settings layout).

### S8: Onboarding wizard (first run)

Steps, progress dots at top, 5 minutes or less:
1. **Welcome:** one sentence of what ScreenTail does; "Nothing is published without your review."
2. **Activate:** enter device code from invite; shows tenant name on success.
3. **Permissions:** microphone (with Windows deep link), capture check, hook check; each shows ✓ or a fix. A microphone Windows blocks offers "Open Windows privacy settings" (`ms-settings:privacy-microphone`) beside "Check again"; one the admin blocked offers no button, since there is nothing to change *(v0.5.5)*.
4. **Your remote tools:** detected tools pre-checked; scope explanation with recommended default.
5. **Connect:** ConnectWise and Hudu (skippable: "You can publish later").
6. **Try it:** 60-second guided test: open any window, click around, say one sentence; see a sample draft in a mini Review.
7. **Done:** "ScreenTail starts when you open a remote session. Look for the pill." Button: Finish.

Re-runnable from Settings → Help. Target (v0.6.0): the welcome step carries the horizontal lockup on the frame (§2.0).

### S9: Web admin (tenant onboarding + dashboard shell)

Pages: Sign up / Sign in · Technicians (invite, seats used/total, device status) · Policy (retention, Local-only enforce, exclusion defaults, redaction patterns, capture scope, telemetry) · Integrations status · Download client · (v1.1) Dashboard: minutes saved/week, edit rate, pending drafts by tech, coverage, redaction stats, remote-tool version drift. Same tokens; light theme default on web; dense tables; no marketing chrome inside the app. The header uses the horizontal lockup, on the frame or on white (§2.0).

### S10: Consent prompt (v1.2)

Appears only when end-user audio is enabled. Modal before recording end-user audio: state-law hint based on tenant/technician state, a one-line script ("This session may be recorded for documentation. Is that okay?"), buttons "Consent given: record" / "Skip end-user audio". Decision and script version written to the audit log.

---

## 6. Notifications and microcopy

| Event | Toast text | Action |
|---|---|---|
| Draft ready | "Draft ready: Printer offline · Acme Dental" | Review |
| Draft pending offline | "Offline: your draft will be created when you're back online." | none |
| Draft failed | "We couldn't draft this session. Your screenshots and transcript are saved." | Open |
| Cost cap reached | "Cloud drafting paused for today. This draft was created on your device." | Review |
| Published | "Published to ticket #48213 · 0:30 logged" | Open in ConnectWise |
| Partial publish | "Note published. Time entry failed: <reason>." | Retry |
| Not confirmed | "Note not confirmed: <reason>." (the reason says to check the ticket first) | Retry, once the check is confirmed |
| Already published | "Already published: check the ticket before publishing again" | Retry, once the check is confirmed |
| Capability | "Microphone is blocked by Windows privacy settings." | Fix |

Copy rules: sentence case; no exclamation marks; name the object (ticket, note, session); always offer the next step; no em dashes (a colon, a comma or a full stop instead).

---

## 7. Accessibility

- WCAG 2.1 AA contrast for all text; verified by a token test in CI, on the panes and on the frame (§2.1).
- Every interactive element has an `AutomationProperties.Name`; lists announce count and position.
- Full keyboard path on every screen; focus order follows visual order; focus ring visible.
- Respect OS "reduce motion" and high-contrast themes (map tokens to system colors when high contrast is on; the frame and the brand band follow §2.1).
- Minimum hit target 28×28 px; HUD controls 32×32.
- Screen-reader text for state pills ("Recording, 12 minutes 41 seconds, 3 items redacted").
- No state is colour alone: each has a word and an icon (§2.0).

---

## 8. Usability metrics the design must hit (measured in usability round and pilot)

- Time from "Draft ready" toast to Publish: median 45 s or less, p75 90 s or less.
- Edit ratio on accepted drafts: under 25%.
- Zero occurrences of "I didn't know it was recording" in pilot interviews.
- Onboarding completion in 5 minutes or less for 3/3 test users.
- Task success without help: pick ticket, exclude a frame, blur a region, change time entry, publish: 90% or more across 5 techs.
- Target layout (v0.6.0): publish the newest draft, ticket suggested correctly, one inferred step, in 2 actions (`Alt+C`, `Ctrl+Enter`), down from 6 in the v0.5 build.

---

## 9. Amendments

| Version | Date | Source | Change |
|---|---|---|---|
| v0.4.1 | 2026-09-11 | ST-014 wireframes, owner decision | Q1 HUD always visible while screen-sharing (S2). Q2 default note type Internal (S3). Q3 tray discard while recording stops capture before confirming (S1). Q4 Publish disabled after a failed draft (S3). Q5 elevated-window HUD wording and tooltip (S2). Q6 bulk-discard confirmation is the session count (S4). Background: `docs/ux/wireframes/README.md`. Q1 and Q2 are still to be checked in the ST-014 technician sessions. |
| v0.4.2 | 2026-09-12 | ST-016 contrast test | The CI token test found 10 text/background pairs under 4.5:1 in the v0.4 palette. Minimal changes: `text.muted` dark `#66748A`→`#7D8B9F`, light `#7B8794`→`#626D79`; `accent.primary` light `#2F6FE0`→`#2A64CC`; new `text.on-accent` (dark theme uses dark text on the bright accent, `#0B0F14`; light stays white). Every pair now 4.7:1 or more. Rule added: state colors are indicators, never body-size text. **Accepted 2026-09-15** (Backlog Part D, D-6). |
| v0.4.3 | 2026-09-13 | ST-015 hi-fi, ST-074 note editor | Three S3 changes, each made because the spec's own rules pointed the other way from its wording. **(a)** The reason a disabled Publish gives moves out of the button's tooltip to a line beside it: a tooltip on a disabled control is the one place a keyboard user can never reach it. **(b)** Offline with no draft reads "There is no note to publish yet. It will be drafted when you are back online." rather than "…Retry the draft first.": retrying a cloud draft with no connection fails again, and §6's copy rule is to always offer the next step. **(c)** The save indicator gains a third label, "Not saved: retrying", beside "Saved" and "Saving…". Neither of the two can be shown truthfully after a failed write, and a pane that looks saved while the edits are only in memory is the worst of the three. **Accepted 2026-09-15** (Backlog Part D, D-6). |
| v0.5.0 | 2026-10-28 | ST-016 modern pass, owner request for the November demo | The tokens move to a Windows 11 look: neutral layered surfaces, a Fluent blue accent, Segoe UI Variable first, a 14 px body, 24 px titles, 6 px control and 8 px card radii, 24 px pane padding and 36 px rows (§2.1 to §2.3). Six tint tokens are added (`bg.hover`, `bg.pressed`, `bg.selected`, `bg.warning`, `bg.danger`, `bg.success`) and every one is a background in the contrast test, so text stays at 4.5:1 and state colours at 3:1 on them; high contrast maps them to the window colour. Section headings move from `text.secondary` to `text.primary` for hierarchy. Every control has rest, hover, pressed, keyboard focus and disabled looks, and a disabled control keeps its words at 4.5:1 instead of fading. The HUD's recording state says "Recording" beside the dot and timer, so it is never colour plus a number alone. Interface copy uses a colon where it used a dash ("Draft ready: Printer offline", "Not capturing: Outlook"). **Awaiting owner review.** |
| v0.5.1 | 2026-11-04 | ST-084 follow-up, keyboard blur | S3's blur was drawn as a drag, which needs a mouse (§7: full keyboard path). On the enlarged frame `B` now also puts a box, a fifth of the frame square, in its middle with focus on it. Arrows move it 1% of the frame, `Shift`+arrows resize it from its top left corner (growing the other way at an edge), `Ctrl` with either takes 10% steps; it stays inside the frame and is at least 2% a side. `Enter` blurs what it covers through the same path as the drag; `Esc` puts it away, and a second `Esc` closes the frame. Its position is announced in percent of the frame, numbers only (INV-10), and an arrow that changes nothing says so. While the box is out and unapplied, `Ctrl+Enter` does not publish; a held `Enter` or `B` counts once; a blur that did not land is said and the box stays for another try. **Awaiting owner review** (the keys are an owner decision). |
| v0.5.2 | 2026-11-08 | ST-093, duplicate-note fix | S3 and §6: a destination that may have landed is "not confirmed" and holds Publish, `Ctrl+Enter` and Retry behind an "I checked the ticket" checkbox, spent on one send. Before, a dropped connection read as a plain failure and the technician could publish the note a second time. |
| v0.5.3 | 2026-11-10 | ST-093, not confirmed outside Review | S1 and S4: a session whose publish was not confirmed says so in History, as a "Not confirmed" badge beside its status, and in the tray's state line between sessions, as a count. History gains "Mark as published" for one such session, asked for first, which settles it without sending anything again. Before, the doubt was visible only inside Review's publish pane, and the only way out of it was to tick the box and publish again. |
| v0.5.4 | 2026-11-11 | ST-016, light control edges | §2.1: fields were outlined in `border.strong` (light `#C3C9D1`, 1.51:1 on `bg.base` and 1.67:1 on `bg.surface`; dark `#4A5058`, 2.10:1 and 1.91:1), below WCAG 1.4.11's 3:1, so a text box was near invisible on a light card. A new `border.control` (dark `#777E88`, light `#80868F`) outlines the text box, password box and combo box in the client and the web app. The token test now holds every control edge (fields, check box, radio, off toggle) to 3:1 against `bg.base`, `bg.surface` and `bg.raised`. Buttons, cards and containers keep `border.strong`; high contrast keeps the system frame colour. |
| v0.5.5 | 2026-11-11 | Design critique of main, copy and state pass (ST-070, ST-074, ST-078, ST-079, ST-083) | Copy and state presentation only, no layout change. S3: result links and Retry are named for the provider their destination went to ("Open in Hudu", "Retry Hudu"), each row's glyph takes its state's colour beside a Sent, Failed, Not confirmed or Already published badge, and the missing company mapping is explained once, in its prompt. S3: Review's Discard says "Stop the session to discard" beside it while the session is captured, and Review with nothing to show says the draft is coming while one is. S1, S2, S4: tools by their registry names, the process in front in its product's spelling ("Not capturing: Outlook"); the shell's status chip gains the state dot (§3). S4: the empty state's next step is the hotkey. S8: the blocked microphone's fix reads "Open Windows privacy settings". Dashes in quoted copy become colons, as v0.5.0 asked. **Awaiting owner review.** |
| v0.6.0 | 2026-11-11 | ST-016, owner-approved brand (Iris, the Ribbon direction) and layout (Concept C, "Outbox") | **Brand (§2.0, new):** Iris `#6A43C4` on the indigo-navy frame `#141030`, flat fills, no gradients; logo files in `shared/design/brand/` with usage rules (grounds, white wordmark on the frame, "Tail" in Iris on white, size ladder, minimum sizes, clear space, tray glyph tiers). **Tokens (§2.1), shipped in this version:** `accent.primary` light `#0F6CBD`→`#6A43C4`, dark `#6CB4FF`→`#B2A6EC`; hover `#572FA7` / `#CBC3F5`; dark `text.on-accent` is ink `#0A071E`; neutrals and tints re-tinted toward the frame (light `bg.base` is paper `#F7F5FE`, `bg.selected` the iris tint); `border.control` `#747A89` in both themes. "Act here" follows the brand. `state.scope` moves from violet (`#7442E0` / `#B39DFF`, ΔE 4.6 from Iris) to slate teal `#3E6E7A` / `#7CCDD6`, keeping its icon and word; offline keeps its blue slate; red, amber and green are unchanged. New `brand.signature`, `brand.ribbon.band`, `brand.ribbon.fold` and the `shell.*` frame group, each with its high-contrast system colour; high contrast stays on system colours. The contrast test gains the frame lists (text at 4.5:1, states at 3:1 on `shell.bg` and `shell.hover`) and holds the band on the frame as decorative (2.80:1, never the only cue) while it stays a 3:1 indicator on the panes. Hudu's violet `#4D2AC9` (ΔE 7.3) is accepted, because "Open in Hudu" is plain link text with the external icon. **Layout targets (§1.1, §3, §5), built by later screen PRs:** the inbox rail grouped To check / Not confirmed / Published today / Earlier (S4); the envelope header To / As / Time / Also with Publish the one filled button, the note edited in place as it will land with evidence on each step, and a receipt after publish with Open next (S3); the pill with Pause, Stop and Mark and an expanded list of event types and times (S2); Settings with a left nav and one Save bar for the whole page (owner decision). Spec Q1 still holds: the pill stays visible to the technician. The v0.5 layouts stay described until each screen's PR lands. Em dashes removed from the spec, as the house style asks. |
| v0.6.1 | 2026-11-11 | ST-078, ST-016: the first Outbox screen PR (envelope and receipt) | S3's envelope header and receipt are built; "As built (v0.6.1)" under S3 records where the build settled what the target left open or differs from it: the envelope is a card over the v0.5 panes until the rail and note PRs; Time stays in whole minutes ("12 min, rounded up to 15") so the typed-minutes check is unchanged; Also lists the ticket note too; the reason and what is still open sit under Publish rather than at the end of the Also line; the receipt's heading has four states with their words (Published, Partly published, Not confirmed, Not published to ticket #N); Retry and Map and publish are secondary so Open next is the receipt's one filled button; Next to check names the tool, duration and start, since History's rows carry no company or title (INV-10), and Today has no time logged yet; mapping needed is a receipt state. The subject line drops "Review:". Three layout tokens (§2.3). `Ctrl+T` and `Enter` on the receipt are built. The brand mark on the receipt plate waits for the asset PR. **Awaiting owner review.** |
| v0.6.4 | 2026-11-12 | ST-016, ST-079: the Outbox inbox rail and workspace frame | S4's rail and the frame round the session pane are built; "As built (v0.6.4)" under S4 records the choices. **(a) INV-10 and the rows.** The rows have no company, ticket or title. INV-10 is about logs and metrics and is not the reason (S3's v0.6.1 note said it was, and is corrected): `list_sessions` carries no note text by ST-079's contract, and the company is not stored on the device. Each row is the tool and age, then duration and screenshots, with a state badge where the group does not say it, and the search looks through tool, state and day words ("Search by tool, day or state"). Showing the note's title and the ticket number read off the window would need them on `list_sessions` (an IPC contract version), and showing the company would need it stored at publish or looked up from the PSA: owner decision. **(b)** Published today goes by the session's start day, since the rows carry no publish time. **(c)** Bulk discard and Mark as published stay in the v0.5 table, now titled All sessions and opened from a third foot item, beside Settings and Diagnostics. **(d)** The open row is the pane's tab with the band on top and a 1 px `shell.accent` edge, the Highlight outline in high contrast; focus on the frame is the pill's 3 px `shell.accent` ring. **(e)** The header tabs go and the status chip moves to the head of the rail in the frame's colours. No title band inside the window: the title bar already names the app, and the band's 48 px left Review too short at the 1024×680 minimum. The title bar's caption colour and glyph are not built yet. Settings' cards and the receipt's company box take the readable width and the side column as their most, not a fixed width, since the pane beside the rail is narrower than either at the minimum. On the enlarged screenshot Close moves up to the heading's row, so OCR text and Blur fit one row under the picture in the narrower screenshots pane; two rows left the picture 63 px tall at the minimum and the blur box too small to show its focus rings. **(f)** Filters and the coverage banner were never built in the v0.5 screen and are not built here; Export audit log stays in Settings, under Privacy. New token `layout.rail-width` (280). `Ctrl+F` and `Esc` in the search are built. **Awaiting owner review.** |
