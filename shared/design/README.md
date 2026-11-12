# Design tokens

`tokens.json` is the single token source (Spec §2). `npm run codegen` here regenerates the WPF resource
dictionaries in `client/ScreenTail.UI/Theme/*.g.xaml` and the web CSS variables; CI fails if the generated
outputs are stale. `ScreenTail.Tests/Theme/ContrastTests` reads this file directly and fails any text colour
under 4.5:1 or state colour under 3:1 on any background in `contrast.backgrounds`, the tints included
(Spec v0.4.2, v0.5.0). High contrast is not generated: `Tokens.HighContrast.xaml` maps every colour key to a
system colour by hand, and `HighContrastTokensTests` fails if it misses one. Built in ST-016.

v0.6.0 (Spec §2.0, §2.1) moves the palette to the Iris brand: `accent.primary` is Iris, out of scope is
slate teal, and three new groups arrive. `shell.*` is the frame (rail, title bar, pill), checked in its own
lists (`shell_text`, `shell_indicators`, `shell_structural`) against `shell_backgrounds`. `brand.signature`
and `brand.ribbon.*` are the brand mark colour and the ribbon band; the band on the frame is decorative
(`shell_decorative`) and stays an indicator on the panes. Each colour's `hc` field names the system colour
`Tokens.HighContrast.xaml` maps it to, and `HighContrastTokensTests` holds the two together.

The logo files are in `brand/`, with a README saying which file goes where.

After `npm run codegen`, copy `web/src/styles/tokens.g.css` to `docs/ux/review-hifi/tokens.g.css` and run
`python3 docs/ux/review-hifi/contrast.py`, so the Review hi-fi stays on the shipped colours.
