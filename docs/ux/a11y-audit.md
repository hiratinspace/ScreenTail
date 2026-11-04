# Accessibility audit (ST-084)

Against Spec §7. Two halves: what a test holds on every run, and what needs a person listening to
Narrator. Since 2026-11-01 everything a test can hold is held, on a real Windows desktop, on the hosted
runner and on the laptop; what is left is listening to what is read.

## Held by CI

### On every commit, on any machine (the Mac suite)

| Spec §7 says | How it is held |
|---|---|
| WCAG 2.1 AA contrast for all text | `ContrastTests`: every text token against every background token in every theme, minimum 4.5:1 |
| Every interactive element has an `AutomationProperties.Name` | `AutomationNamesTests` reads every XAML file under `client/ScreenTail.UI` (the token gallery left out, as a design reference) and fails on a button, box, check, radio, combo, slider, list or grid with no name, no `Content` and no `Header` |
| Live regions are announced | `AutomationNamesTests.EveryLiveRegionIsOneThatIsAnnounced`: no view sets `AutomationProperties.LiveSetting` itself, which WPF never announces; every live region uses `controls:LiveRegion.Setting` |
| Layout lists are not read as lists of type names | `AutomationNamesTests.NoViewLaysOutRowsWithAPlainItemsControl`: repeated rows use `controls:Repeater` |
| The pill is read as its state | `HudStateTests`: "Recording, 12 minutes 41 seconds, 3 screenshots still being redacted", and the announcement leaves the clock out |

### On a real window (the Windows suite, `client/ScreenTail.Tests.Windows/Accessibility`)

Every screen a technician reaches is opened with the screenshot harness's sample data at the shell's
minimum size (1024 x 680), shown and focused, and checked through its automation peers, WPF's own
keyboard pipeline and the axe-windows engine. The screens: Review (ticket chosen, no PSA, published,
partly published, waiting on a company mapping, timeline open, a screenshot enlarged, and the same with the
blur box out over it), History, Settings
(this device, Capture, Privacy, Integrations, Updates), the service-down banner, each of the seven
onboarding steps, the diagnostics panel answered and unanswered, the three typed confirmations, the
shortcut sheet, the pill in all ten states, and the tray menu.

| Rule | Test | What fails it |
|---|---|---|
| Names | `ScreenAccessibilityTests` (`Audit.Names`) | Anything a screen reader reads as a control, list item or image with no name; a name that is a type or a record ("StepDot { Number = 3 }"); an icon font's private-use character; a name over 512 characters |
| Roles | the same | A check box, radio, combo, list, edit, slider, splitter or button exposed as another control type, or without the pattern its role needs (Toggle, SelectionItem, ExpandCollapse, Value, RangeValue, Invoke) |
| Label in name (WCAG 2.5.3) | the same | A control whose visible words are not in its name |
| Hit targets | `Audit.HitTargets` | An enabled control or list row under 28 x 28 as drawn, in device-independent pixels; the pill's under 32 x 32; a tray menu item under 28 px |
| Keyboard reach | `Audit.MouseOnly`, `Audit.TabAsync` | A clickable button that takes no focus; a control Tab never reaches (a list counts as reached through any item, which the arrows move between) |
| No keyboard trap | `Audit.TabAsync` | A Tab that does not move focus, or a round that never comes back to the first stop |
| Focus order | `Audit.TabAsync` | A Tab that moves to something wholly above, or wholly to the left on the same row, at the level where the two branches of the tree part; Shift+Tab not retracing Tab |
| Focus lands on something visible | `Audit.TabAsync` | Focus outside the window, or on a control another element covers |
| Focus is visible | `Audit.TabAsync`, in the dark, light and high-contrast palettes | A focused control that, drawn again without focus, changes in fewer pixels with 3:1 contrast than a quarter of the perimeter of the region around it ((width + height) / 2) |
| Scroll bars can be grabbed | `Audit.CoveredScrollBars` on every screen, `NoSplitterLiesOverAPanesScrollBar` | Anything lying over a visible scroll bar's track (scroll bars are not Tab stops, so the keyboard checks never see them) |
| Documented shortcuts | `KeyboardShortcutTests` | Ctrl+S, Ctrl+Enter, Alt+1/2/3, Alt+T, Alt+C, Alt+Down, Space, Enter, T, Esc (from the strip and from the enlarged view's own buttons), Del and Ctrl+Z not doing what Spec §5 S3 says; ? not opening the shortcut sheet outside a text box, or not typing a ? inside one |
| Blurring from the keyboard | `KeyboardBlurTests` | B on an enlarged screenshot not bringing out the blur box with focus on it; the arrows, Shift+arrows or Ctrl not moving or resizing it as `BlurBox` says (Core's `BlurBoxTests` hold the steps, the 2% minimum and the edges); the box drawn anywhere but over the rectangle it blurs; Enter not blurring the region a drag over the same rectangle blurs, through the same call, or leaving the frame's OCR text; Esc blurring anything, or closing the screenshot before the box is put away; focus not back on the blur toggle afterwards; where the box is not announced as it moves (an arrow into an edge included), or announced with anything but numbers; "Region blurred" not said; a held Enter writing a second region; a blur that did not land going unsaid or throwing the box away; one written but not sent back leaving the old picture or text on screen; a blur the service never answers waiting for ever, or Esc not putting the box away while it waits; the box left out under the OCR text; Ctrl+Enter publishing past an unapplied box; the box drawn off a letterboxed picture; a key the shortcut sheet lists for the box doing something else |
| Undo and Alt codes | `KeyboardShortcutTests` | Ctrl+Z not taking a delete back from the enlarged view; Alt with the number pad moving focus instead of being left to type a character |
| Esc closes dialogs | `KeyboardShortcutTests.EscClosesTheDialog` | A typed confirmation that opens without the caret in its box, or that Esc does not close; the shortcut sheet not closing on Esc |
| Disabled reasons are spoken | `KeyboardShortcutTests.ADisabledPublishSaysWhyToAScreenReader` | A disabled Publish whose help text is not the reason written beside it |
| Live regions | `LiveRegionTests` | The shell's capture state, the service-down banner or the pill's state changing without an announcement; the pill announcing its clock every second |
| Reduce motion | every screen (`ScreenAccessibilityTests`), and `MotionAndContrastTests.WithAnimationsOffNothingMoves` for the timeline and the combo boxes | With Windows' animations off (simulated through `ThemeManager.ClientAreaAnimation`, not changed on the machine): any element animating as a screen opens, as the timeline toggles or as a combo box drops down, a popup that animates, or a storyboard declared in any style or template. The text caret's blink is left to Windows' own cursor settings |
| High contrast | every screen under the high-contrast palette (`ScreenAccessibilityTests`), and `MotionAndContrastTests.InHighContrastEveryColourIsTheSystems` | With Windows' high contrast simulated (`ThemeManager.SystemHighContrast`): the app not switching to the high-contrast palette whatever theme was chosen, or not remembering the choice; a colour token that is not a system colour; any opaque colour drawn on any screen that is not one of the system's |
| Accessibility Insights FastPass, automated checks (AC2) | every screen and pill state, through `ScreenTail.Tests.AxeRunner` | Any error the axe-windows engine reports. It runs in its own process because the engine skips the elements of the process that runs it; it writes no file and prints no window title |

The keyboard is driven without injecting input: `Keys.Press` hands the window's `HwndSource` the key
messages the message loop would, and holds Shift, Ctrl and Alt with `SetKeyboardState`, which changes the
calling thread's keyboard state only. That is why all of this runs on the hosted runner.

### Found and fixed on 2026-11-01

The suite's first runs found these, each now held by the test named beside it:

- **Review's Tab order** went from Discard to the timeline at the bottom, then up to the note, because the
  timeline was declared first (`TabAsync`, reading order).
- **Documented shortcuts that did nothing:** Ctrl+S, Alt+1/2/3, and `?` with no shortcut sheet to open;
  Esc on the enlarged screenshot only worked from the strip (`KeyboardShortcutTests`).
- **The enlarged screenshot left the strip it covers in the Tab order**, so Tab walked through thumbnails
  nobody could see; its buttons ran under the splitter at the smallest window (`TabAsync`, covered focus).
- **Every live region was silent:** WPF declares a live region and never announces it (`LiveRegionTests`).
- **The pill and the shell's badge were named "Capture state"**, which a screen reader read instead of
  the state (`ThePillInEachStateMeetsSpecSection7`, `HudStateTests`).
- **List items, table rows and combo items were read as their records or type names:** the screenshots,
  ticket matches, transcript lines, microphones, speech models, companies and History rows (`Names`).
- **Every layout list was a Tab stop and an unnamed list of type names:** the note's steps and frame
  chips, the wizard's dots and permissions, the diagnostics rows, the publish results, the mappings, the
  sheet's groups; the integration cards' hosts took focus too (`Names`, Accessibility Insights).
- **Icon-font glyphs were read as private-use characters** (`Names`, Accessibility Insights).
- **Buttons repeated on one screen with the same name** (Remove, Change, Test connection, Forget, Save,
  Fix in Windows Settings, Open in ConnectWise), which Accessibility Insights fails (sibling names).
- **Hit targets:** the splitters were 12 px wide, transcript rows 26 px, tray menu items 22 px. The
  splitters are now the 28 px gaps between the panes; a first fix that reached over the panes' edges lay
  on top of their scroll bars (found in review, now a rule).
- **History sorted only with the mouse:** the table's headers took no focus; the filler header after the
  last column was clickable and unnamed.
- **Focus was hard to see** on a screenshot and on a table header, where the focus ring is clipped by the
  list's scrolling layer; both now outline themselves in the accent.
- **The erase confirmation opened without the caret in its box**, and a disabled Publish gave a screen
  reader no reason.
- **The shortcut sheet** (new) did not fit a 768 px screen in one column.
- **Empty text lines** were zero-sized elements Accessibility Insights fails; they now collapse.
- **On the laptop's scaled screen** (its first run, 2026-11-02): a text box's focus in high contrast was
  too faint (its outline thickening into a highlight close in luminance to the frame), as was a table
  header's underline; text boxes now take the focus ring and a focused header is filled with the accent.
  The tray items came to 25.6 px, because their padding was worked out at 96 dpi before the menu had a
  window; they are now fitted at the menu's own DPI, and a test fits them for 150% and 200%.

### What it costs

The Windows assembly ran in about 109 s on the hosted runner before this suite, and in 213 s with it
(run 41366077248). The suite prints its own cost when its collection ends: 88 runs on the UI thread
took 28.7 s, and the 33 Accessibility Insights scans took 112.1 s, about 3.4 s each, most of the
difference. Each screen is opened once for every rule, the second and third palettes re-walk only the
focus visuals, and one axe-windows runner process serves the whole suite. The laptop's capabilities job
runs the whole assembly too: 452 s with the suite (about 430 s before), and the job took 8 min 44 s of
its 15-minute limit (run 41535721423, which passed). With the blur box added as a screen and its keyboard
tests (2026-11-04, run 41901368177), the hosted suite came to 97 runs on the UI thread in 36.1 s and 34
scans in 119.5 s, and the Windows assembly to 263 s.

### The tests have teeth

On 2026-11-01 each rule was broken on purpose on a throwaway branch (three rounds, CI runs 41350929256,
41351907662 and 41365067561, branches since deleted), and the suite failed on each, naming it:

| Sabotage | What failed |
|---|---|
| The ticket search box's name removed | `Names` ("TextBox has no name"), Accessibility Insights `NameNotNull`, and the Mac XAML test |
| Export audit log set to 20 px tall | `HitTargets` ("127 x 20, under 28 x 28") |
| The typed confirmation's box swallows Tab | `TabAsync` ("Tab does not move focus off ... a keyboard trap", and Keep it never reached) |
| The timeline declared before the panes again | `TabAsync` reading order ("... to TextBox Problem, which is above it") |
| History's Open taken out of the Tab order; Install update made unfocusable | "never reached by Tab"; "can be clicked but not reached by the keyboard" |
| The shortcut sheet's Close with no focus visual | "does not look focused", in all three palettes |
| A storyboard fading the shortcut sheet | `WithAnimationsOffNothingMoves` (animating, and a trigger starts a storyboard) |
| A colour written into the diagnostics heading | `InHighContrastEveryColourIsTheSystems` ("#123456, not a system colour") |
| Live regions never watched | all three `LiveRegionTests` |
| Ctrl+S unbound | `ReviewsChordsDoWhatTheSpecSays` |
| Test connection's two buttons sharing a name | Accessibility Insights `SiblingUniqueAndFocusable` |
| Keep it no longer the cancel button | `EscClosesTheDialog(confirm-erase)` |
| The strip left showing under the enlarged screenshot | `TabAsync` ("covered by Border") |
| The splitters reaching over the panes' edges again (run 41365067561) | `CoveredScrollBars` ("the scroll bar of NoteEditorView is covered ... by GridSplitter"), `NoSplitterLiesOverAPanesScrollBar`, and the reading order |
| Alt+NumPad1 jumping to the note again | `AltWithTheNumberPadIsLeftForTyping` |
| Ctrl+Z ignored over the enlarged screenshot | `CtrlZUndoesADeleteFromTheEnlargedView` |
| The blur box's position no longer clamped to the picture (2026-11-04, run 41902899753) | Core's `BlurBoxTests` (three: moved off, grown past, any numbers) and `TheBoxStopsAtThePicturesEdgesAndCanCoverAllOfIt` ("160% from the left, -80% from the top") |
| Enter on the blur box not routed (the same run) | `TheKeysPlaceTheBoxAndEnterBlursWhatItCoversAsADragWould` ("Enter on the blur box was not handled"), `EachKeyDoesWhatTheSheetSays(Return)` |
| From the review of #257, all at once (run 41909379512): a held Enter repeated; a blur that did not land disarmed silently; a blur written but not sent back taken for nothing, and the old picture kept; no timeout; the OCR text leaving the box out; Ctrl+Enter not held; the letterbox offset dropped; no words at an edge | `HoldingEnterBlursOnceAndNeverASecondRegion` (6 regions, not 1), `ABlurThatDidNotLandIsSaid...` (nothing heard), `ABlurWrittenButNotSentBack...` and the pipe test, `ABlurTheServiceNeverAnswers...`, `TheTextPanelAndTheBlurBoxAreNeverOutTogether`, `CtrlEnterDoesNotPublish...` and its routing case, `OnALetterboxedPicture...` (86 against 465.4) and the placement check, `TheBoxStopsAtThePicturesEdges...` ("as far as it goes" missing) |

## Needs a person

What no test can hold is how it sounds. Run on the laptop (or any Windows machine) with Narrator on
(Ctrl+Win+Enter), keyboard only, once per screen in the list above. For each, write down the screen, what
Narrator said, and what it should have said.

1. **Every control is read as its name, then its role, then its state.** Tab through each screen and
   listen: "Search tickets, edit", "Internal, radio button, selected, 1 of 2", "Ticket note, check box,
   checked". Listen for a name that is wrong, not missing (the tests catch missing).
2. **Lists say where you are.** In the screenshots, the transcript, the ticket matches and History, the
   arrows should be read with position ("frame 2, included, 2 of 7"). A History row should be read as its
   summary ("Draft, started ..., 1 min, screenconnect, 7 screenshots").
3. **Live regions are heard.** While Narrator is on: stop the capture service (the banner and the badge
   should be read without moving focus), type a bad hotkey in Settings (the inline message), change step
   in the wizard (the title), discard a frame (the undo toast), publish (the summary), move the blur box
   (where it is, in percent, after each arrow, and "Region blurred" after Enter), and start a session with
   the pill showing (its state, once, not every second).
4. **The pill is read as its state.** With a session running, move Narrator to the pill (Caps+arrow
   keys, scan mode): "Recording, 3 minutes 12 seconds". It never takes focus, by design; its keys are the
   global hotkeys the shortcut sheet lists.
5. **The tray menu from the keyboard.** Win+B, the arrows to the ScreenTail icon, then the menu key or
   Shift+F10: the menu opens, the arrows move through it, each item is read, Enter acts, Esc closes.
6. **Typed confirmations.** Discard a session and Delete everything: the dialog's heading and consequence
   are read when it opens, the caret is in the box, Esc keeps the data.
7. **High contrast for real.** Turn on a Windows contrast theme (Left Alt+Left Shift+Print Screen) with
   the app open: it switches at once, every control's outline and focus ring is visible, and selection in
   lists and History reads as the system's highlight.
8. **Reduce motion for real.** Settings, Accessibility, Visual effects, Animation effects off: nothing in
   the app moves (it has no animations today, and the suite holds that none is added without honouring
   the setting).
9. **Accessibility Insights FastPass by hand** on two windows (Review and Settings), to confirm the
   automated scan sees what the tool sees: zero failures expected. Attach the report to the ticket.

10. **The blur box.** Enlarge a screenshot, press B: Narrator reads "Region to blur, blur box" and the keys
    in its help text, then where it is. Move and resize it, Tab off it and back, Esc, then B and Enter. A
    sighted check at the same time: the box's outline and its focus ring show on a light screenshot and a
    dark one, in the dark, light and a Windows contrast theme.

Known gaps a person should not report as new (owner decisions, below): scroll bars are 12 px.

## Owner decisions (conservative defaults taken 2026-11-01)

- **`?` opens the shortcut sheet only outside a text field**; in a field it types a question mark. The
  sheet is not modal (Spec §4 allows no modal during a session) and Esc closes it.
- **Scroll bars stay 12 px**, an exception to the 28 px target. They are not Tab stops; every scrolled
  region scrolls with the wheel, with keyboard focus moving into view, and with Page keys, which is
  WCAG 2.5.8's "equivalent" exception.
- **Blurring from the keyboard (2026-11-04, the keys are the owner's to confirm).** Spec §5 S3 drew the
  blur as a drag; the first pass left it to the mouse. On an enlarged screenshot B now also brings out a
  box, a fifth of the picture square, in its middle, with focus on it. The arrows move it 1% of the
  picture, Shift with an arrow resizes it from its top left corner (at an edge it grows the other way),
  and Ctrl with either takes 10% steps; it never leaves the picture and is at least 2% a side. Enter
  blurs what it covers through the drag's own path; Esc puts it away from anywhere in the enlarged view,
  and a second Esc closes the screenshot. Its position is announced in percent of the picture, not in
  pixels, so each arrow is one step a listener can count, and it is numbers only (INV-10). The
  alternatives were pixel steps (which would announce uneven numbers and change with the pane's size) and
  a separate chord to start the box (B already means blur in the spec and the sheet). Taken with it, from
  the review of #257, each also the owner's to confirm:
  - **Focus after a blur goes to the blur toggle**, so B or Space blurs another region. A held Enter or
    B is not repeated in the enlarged view (only the first press counts), which is what stops a held
    Enter from bringing the box back through the toggle and blurring a second region nobody placed.
    The alternative was Close, which makes a second blur two keys further away.
  - **Ctrl+Enter (Publish) is held back while a box is out and not applied**, with the reason said;
    publishing then would send the picture the technician was about to cover. Publish's button still
    works with the mouse, which is a deliberate act.
  - **An arrow that changes nothing says so** ("..., as far as it goes"), each time, so a listener can
    tell an edge from a missed key.
  - **Resize is from the top left corner only**, growing the other way at the right or bottom edge. Moving
    then resizing reaches any rectangle; a second corner would need a second set of chords.
  - **A blur that did not land is said and the box stays where it was**, armed, for another try. One the
    service wrote but could not send back is said as blurred, and the pane stops showing the old picture
    and its text. One the service does not answer in 20 s is said as unknown, and the picture and text,
    which may be stale, are dropped until the screenshot is opened again. Esc while a blur waits puts the
    box away at once; what came of it is still said.
  - **The box's focus rings are inside it**, so nothing drawn reaches past what is blurred: the outer edge
    is the edge of the blur, and the help text says so.
- **History's headers are Tab stops** so the table sorts from the keyboard (Spec §3: sortable). That adds
  five stops before the rows; the alternative is a sort chord, which the spec does not name.
- **The gaps between Review's panes are 28 px** (were 12), because the splitter is the gap: Spec §7's
  target size without lying over a pane's scroll bar.
- **The shortcut sheet lists the shipped capture hotkeys** and says to change them in Settings; reading the
  technician's own chords would need the service's settings every time the sheet opens.
- **The caret's blink** is not suppressed by reduce motion; Windows' own text cursor settings govern it.

What to record from the manual pass: the screen, the finding, and whether it is a name (fix the XAML; the
suite will hold it), an order (fix the tree or `TabIndex`), or a size (fix the style in
`Components.xaml`, which fixes it everywhere).
