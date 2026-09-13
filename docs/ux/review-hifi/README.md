# Review screen — hi-fi design (ST-015)

The wireframes (ST-014, `../wireframes/s3-review.html`) settled layout, content and states in
greyscale. This is the same screen with the visual design committed: real colour, type scale, spacing and
state treatment, built against the design tokens and rendered in both themes.

**Open it:** `open docs/ux/review-hifi/index.html`. The toolbar switches between the eight states and the
two themes, and a state can be deep-linked: `index.html?state=purged&theme=light`.

## Why it is HTML built on the shipped tokens

`review.css` uses no colour of its own. Every value comes from `tokens.g.css`, which is generated from
`shared/design/tokens.json` — the same source the WPF resource dictionaries are generated from. A
prototype with its own palette looks decided and is a lie, because the thing that ships uses different
colours; this one cannot drift from the product without the product drifting too. Sizes, spacing and radii
are the token scale restated as custom properties, so the stylesheet reads like the spec (`size.lg`,
`space.4`) rather than like a pile of pixels.

That is also what makes the contrast report a measurement rather than a claim: `contrast.py` reads the
same file the CI contrast test reads.

## The eight states

| State | Deep link | What it settles |
|---|---|---|
| Default | `?state=default` | The finished note with frame chips and a transcript quote; 7 of 14 frames included; a suppressed interval shown as a gap in the strip, not a missing frame |
| Low-confidence step | `?state=low-confidence` | Marked three ways — ⚠ glyph, the word "Inferred", a left rule — so it reads without colour (Spec §7); `Alt+C` confirms |
| Partial capture | `?state=partial` | Warning banner: "Capture started late or was interrupted. Some steps may be missing." |
| Frames purged | `?state=purged` | Warning banner with the count; the strip header says "7 of 11 included · 3 removed" so the arithmetic is visible |
| Offline | `?state=offline` | Info banner; Publish disabled with the reason beside it, not in a tooltip |
| Draft failed | `?state=draft-failed` | The note pane says what happened and offers Retry; the screenshots and transcript stay; Publish disabled with "There is no note to publish yet" (v0.4.1, Q4) |
| Publish partly failed | `?state=partial-publish` | Per-destination result list: note added, time entry rejected with the PSA's reason, Retry on the failed one only |
| Published | `?state=published` | Title bar gains the badge; the publish pane becomes the result list with "Open in ConnectWise" |

Screenshots of every state in both themes are in `screens/`, rendered with headless Chrome from these
exact deep links.

## Decisions the hi-fi makes that the wireframes did not

- **State colours are indicators, never text.** Spec v0.4.2 says so, and the contrast report is why: the
  first version of this design used `confidence.low` for the warning sentence and `state.published` for a
  badge's words, and both failed AA on the light surface (3.19:1 and 3.30:1). The colour now carries a
  rule, a border or a glyph; the words are `text.secondary`. See `contrast.md`.
- **Section headings are `size.lg` semibold in `text.secondary`**, per Spec §5 S3, which keeps the
  technician's own words — the step text — as the darkest thing on the page.
- **A suppressed interval is a striped, dashed gap in the filmstrip**, the same width as a frame, with
  the reason and the times on it. A technician has to be able to see that something was deliberately not
  captured; otherwise the note reads as if it simply lost eighteen seconds.
- **Disabled Publish always says why, next to the button.** A tooltip on a disabled control is the one
  place a keyboard user can never reach it.
- **The Discard action is a ghost button that turns `state.danger` only on hover**, so it is findable
  without being inviting. The typed-confirmation dialog ("DISCARD") is the wireframes' and is unchanged.
- **The timeline scrubber** shows scope as a translucent `state.scope` band and suppression as
  `state.paused` stripes, both under the marks, so a click dot and a frame square stay legible on top.

## Shortcut map (Spec §5 S3, verbatim)

| Chord | Does |
|---|---|
| `Ctrl+Enter` | Publish |
| `Ctrl+S` | Save now |
| `Alt+1` / `Alt+2` / `Alt+3` | Focus note / screenshots / publish pane |
| `Alt+T` | Toggle the timeline panel |
| `Alt+C` | Confirm the focused low-confidence step |
| `Alt+↑` / `Alt+↓` | Reorder the focused step |
| `Space` | Include / exclude the focused frame |
| `B` | Blur a region of the enlarged frame |
| `Del` | Delete the focused frame (5 s undo toast) |
| `Enter` | Enlarge the focused frame; at the end of a step, new step |
| `Esc` | Close the enlarged frame |
| `?` | Shortcut sheet |

Every pane's footer shows the chords that apply to it, so the map is on screen where it is used rather
than only in a sheet.

## What this does not decide

Whether the drafted words are any good — that is ST-061's prompt and ST-062's evaluation. The note text
here is a plausible example, not a fixture, and ST-074 builds against `research/fixtures/handcrafted/`.

## Contrast

`contrast.md` — 32 pairs, all passing. Regenerate after any token change with
`python3 docs/ux/review-hifi/contrast.py`; it exits non-zero on a failure, so it can sit in CI.
