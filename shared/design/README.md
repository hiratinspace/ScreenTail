# Design tokens

`tokens.json` is the single token source (Spec §2). `npm run codegen` here regenerates the WPF resource
dictionaries in `client/ScreenTail.Shared` and the web CSS variables; CI fails if the generated outputs
are stale. `ScreenTail.Tests/Theme/ContrastTests` reads this file directly and fails any text/background
pair under 4.5:1 (Spec v0.4.2). Built in ST-016.
