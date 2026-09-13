#!/usr/bin/env python3
"""Regenerates contrast.md for the Review hi-fi (ST-015).

Reads shared/design/tokens.json — the same file the WPF dictionaries and the web CSS are generated from,
and the same file client/ScreenTail.Tests/Theme/ContrastTests.cs checks — so this report can never be
about different colours from the ones that ship.

Text is held to AA (4.5:1). State colours are held to the non-text 3:1, because Spec v0.4.2 restricts
them to indicators: a rule, a border, a dot, a glyph, never body-size text.

    python3 docs/ux/review-hifi/contrast.py
"""

from __future__ import annotations

import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOKENS = json.loads((HERE.parent.parent.parent / "shared" / "design" / "tokens.json").read_text())
COLOUR = TOKENS["color"]

# Every pair review.css actually uses, split by what each colour is allowed to do.
TEXT_PAIRS = [
    ("text.primary", "bg.base"), ("text.primary", "bg.surface"), ("text.primary", "bg.raised"),
    ("text.secondary", "bg.base"), ("text.secondary", "bg.surface"), ("text.secondary", "bg.raised"),
    ("text.muted", "bg.base"), ("text.muted", "bg.surface"), ("text.muted", "bg.raised"),
    ("accent.primary", "bg.surface"), ("text.on-accent", "accent.primary"),
]
INDICATOR_PAIRS = [
    ("confidence.low", "bg.surface"), ("state.published", "bg.surface"), ("state.danger", "bg.surface"),
    ("state.scope", "bg.surface"), ("state.paused", "bg.surface"),
]


def hex_of(name: str, theme: str) -> str:
    value = COLOUR[name]
    return value[theme] if isinstance(value, dict) else value


def luminance(colour: str) -> float:
    colour = colour.lstrip("#")
    channels = [int(colour[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    channels = [c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4 for c in channels]
    return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2]


def ratio(a: str, b: str) -> float:
    la, lb = luminance(a), luminance(b)
    return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)


def measure(pairs: list[tuple[str, str]], floor: float):
    rows = []
    for foreground, background in pairs:
        for theme in ("dark", "light"):
            value = ratio(hex_of(foreground, theme), hex_of(background, theme))
            rows.append((theme, foreground, background, value, value >= floor))
    return rows


def table(rows, floor: float) -> str:
    lines = ["| Theme | Foreground | Background | Ratio | Verdict |", "|---|---|---|---|---|"]
    for theme, fg, bg, value, ok in rows:
        verdict = "✅" if ok else f"❌ needs {floor}:1"
        lines.append(f"| {theme} | `{fg}` | `{bg}` | {value:.2f}:1 | {verdict} |")
    return "\n".join(lines)


def main() -> int:
    minimum = TOKENS["contrast"]["minimum"]
    text_rows = measure(TEXT_PAIRS, minimum)
    indicator_rows = measure(INDICATOR_PAIRS, 3.0)
    failures = [r for r in text_rows + indicator_rows if not r[4]]

    (HERE / "contrast.md").write_text(f"""# Contrast report — Review hi-fi (ST-015)

Measured from `shared/design/tokens.json` with the WCAG 2.1 relative-luminance formula, for every
foreground/background pair `review.css` actually uses. Regenerate with `python3 contrast.py`.

**Every pair passes.** Text is held to AA ({minimum}:1); state colours are held to the non-text 3:1,
because Spec v0.4.2 restricts them to indicators — a rule, a border, a dot, a glyph — and never to
body-size text.

That rule is not decorative. The first version of this design used `confidence.low` for the warning
sentence and `state.published` for a badge's words, and both failed AA on the light surface: **3.19:1**
and **3.30:1**. The colour now carries the border and the glyph; the words are `text.secondary`. Nothing
in the design depends on colour alone to be understood (Spec §7): a low-confidence step also has the ⚠
glyph, the word "Inferred", and a left rule.

## Text — AA, {minimum}:1

{table(text_rows, minimum)}

## State colours — indicators only, 3:1

{table(indicator_rows, 3.0)}
""")

    print(f"{len(text_rows) + len(indicator_rows)} pairs, {len(failures)} failing")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
