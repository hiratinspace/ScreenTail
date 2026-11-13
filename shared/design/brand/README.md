# ScreenTail logo files (Iris, Spec v0.6.0)

The refined ScreenTail mark: one ribbon hook, drawn once and turned 180 degrees, folds into an S around an
upright 4:3 screen. Every file is a flat-fill vector on a 64-unit grid (80 units for the tiles), with no
gradients and no background plate unless the name says so. Colours are the brand tokens in
`../tokens.json`: Iris #6A43C4, Ribbon 400 #B2A6EC, frame #141030, paper #F7F5FE, ink #0A071E. The rules
for using them (sizes, clear space, grounds, the wordmark) are in `Build Plan/01-UX-Design-Spec.md` §2.0.

The app's `.ico` and the tray icons are built from these files; see "Building the icons" below.

## Marks

| File | Use |
|---|---|
| `mark-primary-on-navy.svg` | Full-colour mark on the frame (installer, website, About), 56 px and up |
| `mark-primary-on-white.svg` | Full-colour mark on white or paper, 56 px and up |
| `mark-primary-transparent-light.svg` | Full-colour mark with no plate, for light surfaces inside the app at 56 px and up |
| `mark-flat-on-navy.svg` | Flat mark on the frame, where the full mark's detail would be lost |
| `mark-flat-on-white.svg` | Flat mark on white |
| `mark-mono-black.svg` | One colour, black, for print; the screen stays knocked out |
| `mark-mono-white.svg` | One colour, white (reversed), for print on dark grounds |

## Glyphs (the size ladder)

| File | Use |
|---|---|
| `glyph-mid-on-navy.svg` | Mid glyph, 28 to 48 px, two note lines, on the frame (the receipt's 40 px mark, drawn in the tokens since v0.6.12) |
| `glyph-small-light.svg` | Small glyph, 16 to 24 px, ribbon and solid screen only, on light grounds |
| `glyph-small-dark.svg` | Small glyph, 16 to 24 px, Ribbon 400, on dark grounds and the frame (the title bar glyph) |

## App icon

| File | Use |
|---|---|
| `app-icon-256.svg` | App icon tile (frame, full mark), 32 px and up: the .ico at 64, 128 and 256 |
| `app-icon-small.svg` | App icon tile with the small glyph, 16 to 24 px: the .ico at 16, 20 and 24 |

The .ico packs the small glyph at 16, 20 and 24, the mid glyph at 32 and 48, and the full mark at 64, 128
and 256.

## Lockups

The wordmark is Exo 2 ExtraBold (Natanael Gama, SIL Open Font License 1.1) converted to outlines, tracking
-20/1000, so no font needs installing. On the frame the whole word is white; on white, "Tail" is Iris.

| File | Use |
|---|---|
| `lockup-horizontal-on-navy.svg` | Horizontal lockup on the frame; mark at least 24 px tall (about 120 px wide) |
| `lockup-horizontal-on-white.svg` | Horizontal lockup on white, "Tail" in Iris |
| `lockup-horizontal-mono-black.svg` | Horizontal lockup, one colour, for print |
| `lockup-stacked-on-navy.svg` | Stacked lockup on the frame; mark at least 64 px |
| `lockup-stacked-on-white.svg` | Stacked lockup on white |

## Tray icons

Four states, each for the light taskbar (#F3F3F3) and the dark taskbar (#202020), at 16 px (100% scaling)
and 32 px (200%). State never relies on colour alone: recording adds a filled dot, paused two bars, offline
turns the glyph grey and adds a cross. Each badge is cut into the glyph with a transparent ring.

| File | Use |
|---|---|
| `tray-idle-light-16.svg`, `tray-idle-light-32.svg` | Idle, light taskbar: Iris glyph (5.89:1) |
| `tray-idle-dark-16.svg`, `tray-idle-dark-32.svg` | Idle, dark taskbar: Ribbon 400 glyph (7.42:1) |
| `tray-recording-light-16.svg`, `tray-recording-light-32.svg` | Recording, light taskbar: dot #D3302F |
| `tray-recording-dark-16.svg`, `tray-recording-dark-32.svg` | Recording, dark taskbar: dot #FF6B6B |
| `tray-paused-light-16.svg`, `tray-paused-light-32.svg` | Paused, light taskbar: bars #B35900 |
| `tray-paused-dark-16.svg`, `tray-paused-dark-32.svg` | Paused, dark taskbar: bars #F5A524 |
| `tray-offline-light-16.svg`, `tray-offline-light-32.svg` | Offline, light taskbar: blue slate #6A7380 and a cross |
| `tray-offline-dark-16.svg`, `tray-offline-dark-32.svg` | Offline, dark taskbar: blue slate #A2AAB5 and a cross |

Draft ready has no file here. The spec gives it the idle glyph with a badge (§2.0, tray glyph tiers), so
`build-icons.py` builds it from the idle file: the same transparent ring the other states cut in, and a
filled rounded square in the screen's colour (frame #141030 on the light taskbar, paper #F7F5FE on the
dark one). The count of drafts is in the tooltip and the menu's first row.

## Building the icons

```
python3 shared/design/brand/build-icons.py
```

Needs Python 3.10+, Pillow and Google Chrome or Chromium (`--chrome PATH` if it is not in the usual
place). It renders every size from its own SVG at exact pixels in headless Chrome, which draws the
masks correctly, and writes:

| Output | Frames |
|---|---|
| `client/ScreenTail.UI/Assets/app.ico` | 16, 20, 24 from `app-icon-small.svg`; 32, 40, 48 from `glyph-mid-on-navy.svg`; 64, 128, 256 from `app-icon-256.svg` |
| `client/ScreenTail.UI/Assets/Tray/tray-<state>-<light\|dark>.ico` | 16, 20, 24 from the `-16` file; 32, 40, 48 from the `-32` file |

Frames below 256 are 32-bit BMP, the 256 frame is PNG. The UI uses `app.ico` as the executable's icon and
on every window (`Window.Base`), and the tray loads the state's icon at the screen's small-icon size (16 at
100%, 20 at 125%, 24 at 150%, 32 at 200%) for the taskbar's theme (`SystemUsesLightTheme`). Run the script
again after changing any of these SVGs and commit the .ico files with them. The gallery render
(`--gallery --screenshot`) writes `brand-icons.png` with every frame, for review in the CI artifact.
