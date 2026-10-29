# Design tokens

`tokens.json` is the single token source (Spec §2). `npm run codegen` here regenerates the WPF resource
dictionaries in `client/ScreenTail.UI/Theme/*.g.xaml` and the web CSS variables; CI fails if the generated
outputs are stale. `ScreenTail.Tests/Theme/ContrastTests` reads this file directly and fails any text colour
under 4.5:1 or state colour under 3:1 on any background in `contrast.backgrounds`, the tints included
(Spec v0.4.2, v0.5.0). High contrast is not generated: `Tokens.HighContrast.xaml` maps every colour key to a
system colour by hand, and `HighContrastTokensTests` fails if it misses one. Built in ST-016.
