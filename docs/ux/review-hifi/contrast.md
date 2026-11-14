# Contrast report: Review hi-fi (ST-015)

Measured from `shared/design/tokens.json` with the WCAG 2.1 relative-luminance formula, for every
foreground/background pair `review.css` actually uses. Regenerate with `python3 contrast.py`.

**Every pair passes.** Text is held to AA (4.5:1); state colours are held to the non-text 3:1,
because Spec v0.4.2 restricts them to indicators (a rule, a border, a dot, a glyph) and never to
body-size text.

That rule is not decorative. The first version of this design used `confidence.low` for the warning
sentence and `state.published` for a badge's words, and both failed AA on the light surface: **3.19:1**
and **3.30:1**. The colour now carries the border and the glyph; the words are `text.secondary`. Nothing
in the design depends on colour alone to be understood (Spec §7): a low-confidence step also has the ⚠
glyph, the word "Inferred", and a left rule.

## Text: AA, 4.5:1

| Theme | Foreground | Background | Ratio | Verdict |
|---|---|---|---|---|
| dark | `text.primary` | `bg.base` | 14.67:1 | ✅ |
| light | `text.primary` | `bg.base` | 16.42:1 | ✅ |
| dark | `text.primary` | `bg.surface` | 13.72:1 | ✅ |
| light | `text.primary` | `bg.surface` | 17.73:1 | ✅ |
| dark | `text.primary` | `bg.raised` | 12.38:1 | ✅ |
| light | `text.primary` | `bg.raised` | 16.52:1 | ✅ |
| dark | `text.secondary` | `bg.base` | 9.74:1 | ✅ |
| light | `text.secondary` | `bg.base` | 9.63:1 | ✅ |
| dark | `text.secondary` | `bg.surface` | 9.10:1 | ✅ |
| light | `text.secondary` | `bg.surface` | 10.39:1 | ✅ |
| dark | `text.secondary` | `bg.raised` | 8.21:1 | ✅ |
| light | `text.secondary` | `bg.raised` | 9.69:1 | ✅ |
| dark | `text.muted` | `bg.base` | 6.35:1 | ✅ |
| light | `text.muted` | `bg.base` | 6.31:1 | ✅ |
| dark | `text.muted` | `bg.surface` | 5.94:1 | ✅ |
| light | `text.muted` | `bg.surface` | 6.81:1 | ✅ |
| dark | `text.muted` | `bg.raised` | 5.36:1 | ✅ |
| light | `text.muted` | `bg.raised` | 6.35:1 | ✅ |
| dark | `accent.primary` | `bg.surface` | 7.24:1 | ✅ |
| light | `accent.primary` | `bg.surface` | 6.54:1 | ✅ |
| dark | `text.on-accent` | `accent.primary` | 9.00:1 | ✅ |
| light | `text.on-accent` | `accent.primary` | 6.54:1 | ✅ |

## State colours: indicators only, 3:1

| Theme | Foreground | Background | Ratio | Verdict |
|---|---|---|---|---|
| dark | `confidence.low` | `bg.surface` | 9.53:1 | ✅ |
| light | `confidence.low` | `bg.surface` | 4.83:1 | ✅ |
| dark | `state.published` | `bg.surface` | 8.62:1 | ✅ |
| light | `state.published` | `bg.surface` | 5.41:1 | ✅ |
| dark | `state.danger` | `bg.surface` | 7.26:1 | ✅ |
| light | `state.danger` | `bg.surface` | 5.66:1 | ✅ |
| dark | `state.scope` | `bg.surface` | 8.76:1 | ✅ |
| light | `state.scope` | `bg.surface` | 5.65:1 | ✅ |
| dark | `state.paused` | `bg.surface` | 7.80:1 | ✅ |
| light | `state.paused` | `bg.surface` | 4.83:1 | ✅ |
