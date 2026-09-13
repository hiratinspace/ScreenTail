# Contrast report — Review hi-fi (ST-015)

Measured from `shared/design/tokens.json` with the WCAG 2.1 relative-luminance formula, for every
foreground/background pair `review.css` actually uses. Regenerate with `python3 contrast.py`.

**Every pair passes.** Text is held to AA (4.5:1); state colours are held to the non-text 3:1,
because Spec v0.4.2 restricts them to indicators — a rule, a border, a dot, a glyph — and never to
body-size text.

That rule is not decorative. The first version of this design used `confidence.low` for the warning
sentence and `state.published` for a badge's words, and both failed AA on the light surface: **3.19:1**
and **3.30:1**. The colour now carries the border and the glyph; the words are `text.secondary`. Nothing
in the design depends on colour alone to be understood (Spec §7): a low-confidence step also has the ⚠
glyph, the word "Inferred", and a left rule.

## Text — AA, 4.5:1

| Theme | Foreground | Background | Ratio | Verdict |
|---|---|---|---|---|
| dark | `text.primary` | `bg.base` | 16.27:1 | ✅ |
| light | `text.primary` | `bg.base` | 16.95:1 | ✅ |
| dark | `text.primary` | `bg.surface` | 15.37:1 | ✅ |
| light | `text.primary` | `bg.surface` | 18.05:1 | ✅ |
| dark | `text.primary` | `bg.raised` | 14.20:1 | ✅ |
| light | `text.primary` | `bg.raised` | 16.20:1 | ✅ |
| dark | `text.secondary` | `bg.base` | 7.84:1 | ✅ |
| light | `text.secondary` | `bg.base` | 6.84:1 | ✅ |
| dark | `text.secondary` | `bg.surface` | 7.41:1 | ✅ |
| light | `text.secondary` | `bg.surface` | 7.28:1 | ✅ |
| dark | `text.secondary` | `bg.raised` | 6.85:1 | ✅ |
| light | `text.secondary` | `bg.raised` | 6.54:1 | ✅ |
| dark | `text.muted` | `bg.base` | 5.55:1 | ✅ |
| light | `text.muted` | `bg.base` | 4.95:1 | ✅ |
| dark | `text.muted` | `bg.surface` | 5.25:1 | ✅ |
| light | `text.muted` | `bg.surface` | 5.27:1 | ✅ |
| dark | `text.muted` | `bg.raised` | 4.85:1 | ✅ |
| light | `text.muted` | `bg.raised` | 4.73:1 | ✅ |
| dark | `accent.primary` | `bg.surface` | 5.65:1 | ✅ |
| light | `accent.primary` | `bg.surface` | 5.54:1 | ✅ |
| dark | `text.on-accent` | `accent.primary` | 5.97:1 | ✅ |
| light | `text.on-accent` | `accent.primary` | 5.54:1 | ✅ |

## State colours — indicators only, 3:1

| Theme | Foreground | Background | Ratio | Verdict |
|---|---|---|---|---|
| dark | `confidence.low` | `bg.surface` | 10.88:1 | ✅ |
| light | `confidence.low` | `bg.surface` | 3.19:1 | ✅ |
| dark | `state.published` | `bg.surface` | 7.97:1 | ✅ |
| light | `state.published` | `bg.surface` | 3.30:1 | ✅ |
| dark | `state.danger` | `bg.surface` | 6.57:1 | ✅ |
| light | `state.danger` | `bg.surface` | 4.83:1 | ✅ |
| dark | `state.scope` | `bg.surface` | 4.29:1 | ✅ |
| light | `state.scope` | `bg.surface` | 5.70:1 | ✅ |
| dark | `state.paused` | `bg.surface` | 8.46:1 | ✅ |
| light | `state.paused` | `bg.surface` | 3.19:1 | ✅ |
