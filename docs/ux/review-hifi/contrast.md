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
| dark | `text.primary` | `bg.base` | 15.34:1 | ✅ |
| light | `text.primary` | `bg.base` | 15.34:1 | ✅ |
| dark | `text.primary` | `bg.surface` | 13.99:1 | ✅ |
| light | `text.primary` | `bg.surface` | 16.88:1 | ✅ |
| dark | `text.primary` | `bg.raised` | 12.44:1 | ✅ |
| light | `text.primary` | `bg.raised` | 15.74:1 | ✅ |
| dark | `text.secondary` | `bg.base` | 10.05:1 | ✅ |
| light | `text.secondary` | `bg.base` | 7.98:1 | ✅ |
| dark | `text.secondary` | `bg.surface` | 9.17:1 | ✅ |
| light | `text.secondary` | `bg.surface` | 8.78:1 | ✅ |
| dark | `text.secondary` | `bg.raised` | 8.15:1 | ✅ |
| light | `text.secondary` | `bg.raised` | 8.19:1 | ✅ |
| dark | `text.muted` | `bg.base` | 7.03:1 | ✅ |
| light | `text.muted` | `bg.base` | 5.59:1 | ✅ |
| dark | `text.muted` | `bg.surface` | 6.42:1 | ✅ |
| light | `text.muted` | `bg.surface` | 6.15:1 | ✅ |
| dark | `text.muted` | `bg.raised` | 5.71:1 | ✅ |
| light | `text.muted` | `bg.raised` | 5.74:1 | ✅ |
| dark | `accent.primary` | `bg.surface` | 7.12:1 | ✅ |
| light | `accent.primary` | `bg.surface` | 5.38:1 | ✅ |
| dark | `text.on-accent` | `accent.primary` | 8.38:1 | ✅ |
| light | `text.on-accent` | `accent.primary` | 5.38:1 | ✅ |

## State colours — indicators only, 3:1

| Theme | Foreground | Background | Ratio | Verdict |
|---|---|---|---|---|
| dark | `confidence.low` | `bg.surface` | 9.32:1 | ✅ |
| light | `confidence.low` | `bg.surface` | 4.83:1 | ✅ |
| dark | `state.published` | `bg.surface` | 8.43:1 | ✅ |
| light | `state.published` | `bg.surface` | 5.41:1 | ✅ |
| dark | `state.danger` | `bg.surface` | 7.10:1 | ✅ |
| light | `state.danger` | `bg.surface` | 5.66:1 | ✅ |
| dark | `state.scope` | `bg.surface` | 6.81:1 | ✅ |
| light | `state.scope` | `bg.surface` | 5.84:1 | ✅ |
| dark | `state.paused` | `bg.surface` | 7.63:1 | ✅ |
| light | `state.paused` | `bg.surface` | 4.83:1 | ✅ |
