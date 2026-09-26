# Accessibility audit (ST-084)

Against Spec §7. Two halves: what a test can hold on every commit, and what needs a Windows session
with Narrator and Accessibility Insights. The first is done and enforced; the second is listed with
its checklist for whoever runs it.

## Held by CI

| Spec §7 says | How it is held |
|---|---|
| WCAG 2.1 AA contrast for all text | `ContrastTests`: every text token against every background token in every theme, minimum 4.5:1 |
| Every interactive element has an `AutomationProperties.Name` | `AutomationNamesTests` reads every XAML file under `client/ScreenTail.UI` (the token gallery excluded — it is a design reference, not a screen) and fails on a button, box, check, radio, combo, slider, list or grid with no name, no `Content` and no `Header`. 2026-09-26: three were missing — the confirmation dialog's typed box and its button, whose content is set in code, and the filmstrip list — fixed in the same change |
| Screen-reader text for state pills | The HUD's pill and the shell's status badge carry `AutomationProperties.Name` and a `LiveSetting`; the wording is in the view model with the rest of Spec §5's copy |
| Lists announce count and position | WPF's `ListBox` and `DataGrid` announce these through UI Automation on their own; the filmstrip's header says "7 of 14 included" beside it |
| Respect high-contrast themes | `ThemeManager.Apply` substitutes the high-contrast dictionary whenever Windows has it on, whatever the technician chose; the harness renders every screen in it |

Live regions: the status badge, the banner, the publish summary, the mapping prompt, every inline
validation message and the wizard's step title are `LiveSetting="Polite"` or `Assertive` so a change
is read without the whole window being re-announced.

## Needs Windows

Run on the laptop with Narrator on and Accessibility Insights for Windows installed, once per screen
(Review, History, Settings with its four sections, the onboarding wizard's seven steps, the
confirmation dialog, the diagnostics panel, the HUD pill in each state, the tray menu):

1. **Keyboard only.** Unplug the mouse. Every action Spec §5 lists reaches by keyboard; `?` opens the
   shortcut sheet; `Esc` closes what it says it closes. Note anything that needs the mouse.
2. **Focus order follows visual order** and the focus ring is visible on every control in every
   theme. Tab through each screen top to bottom and write down any jump.
3. **Narrator reads each control as its name**, not as "button" or "edit". The names come from the
   table above; listen for one that is wrong rather than missing.
4. **Hit targets** are 28 × 28 px or more, the HUD's 32 × 32. Accessibility Insights' "Tab stops" and
   "FastPass" give the numbers.
5. **Reduce motion**: with Windows' animation effects off, the timeline panel opens without its
   transition and nothing else animates.
6. **Accessibility Insights FastPass** on each window: zero critical findings (AC2). Attach the
   report to the ticket.

What to record: the screen, the finding, and whether it is a missing name (fix the XAML, the test
will hold it), a focus-order problem (fix `TabIndex` or the visual order) or a hit-target problem
(fix the style in `Components.xaml`, which fixes it everywhere).
