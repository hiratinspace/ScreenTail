"""Writes unicode-tables.json: the character data both draft validators read text with (ST-061).

checks.py runs on Python's re and DraftValidator.cs on .NET's Regex, and the two do not agree about
characters. Python's \\s takes U+001C to U+001F and .NET's does not; their \\w and \\b disagree about
combining marks, connector punctuation and other-number characters; Python's IGNORECASE folds the dotted
and dotless i and the long s into ASCII and .NET's does not. And the API runs with InvariantGlobalization,
where string.Normalize hands non-ASCII text back unchanged, so .NET has no NFKC of its own in production.

So neither validator asks its platform. Both read this file:

- The NFKC data (decompositions, combining classes, compositions), taken from Python's unicodedata. Both
  validators normalise with the same few lines of code over these tables, so they give the same answer
  whatever Unicode version their platform has.
- The characters .NET's Regex reads as a word character where \\b is concerned ("word"), as a digit
  ("digit_zeros", each the zero of ten), and as white space ("net_space"), asked of .NET itself through
  engine_probe.cs. The shared patterns spell these out instead of writing \\b, \\d and \\s, so Python's re
  reads them as .NET does, and the server reads them as it did before ST-061.
- What normalisation removes ("unseen": every format character) and makes a plain space ("spaces": every
  Unicode space and separator, and U+001C to U+001F).
- "assigned", the code points the NFKC data's Unicode version knows. Only the tests read it.

    .venv/bin/python prompts/unicode_tables.py           # writes prompts/unicode-tables.json
    .venv/bin/python prompts/unicode_tables.py --check   # every code point through both validators

Both need the .NET SDK. --check reads every code point on its own through checks.read and
DraftValidator.Read and compares the two, and compares every shared class, and every single-character
piece of a shared pattern, on every UTF-16 code unit a reading can hold, in the two engines with and
without IgnoreCase.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import unicodedata
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "unicode-tables.json"
PROBE = HERE / "engine_probe.cs"

S_BASE, S_COUNT = 0xAC00, 11172

COMMENT = [
    "Character data both draft validators read text with. Written by unicode_tables.py; do not edit.",
    "",
    "checks.py and DraftValidator.cs both normalise with these tables rather than their platform's,",
    "because the API runs with InvariantGlobalization, where .NET's string.Normalize does nothing to",
    "non-ASCII text, and because Python's and .NET's Unicode versions differ. 'word', 'digit_zeros' and",
    "'net_space' are what .NET's Regex reads as \\b's word characters, \\d and \\s, asked of .NET itself;",
    "the shared patterns spell them out so that Python's re reads them as .NET does. 'unseen' is removed,",
    "'spaces' become a plain space. 'assigned' is read only by the tests. See unicode_tables.py.",
]


def ranges(code_points) -> str:
    """Code points as space-separated hex ranges, "0041-005A 0061-007A"."""
    points = sorted(set(code_points))
    out, i = [], 0
    while i < len(points):
        start = end = points[i]
        while i + 1 < len(points) and points[i + 1] == end + 1:
            i += 1
            end = points[i]
        out.append(f"{start:04X}" if start == end else f"{start:04X}-{end:04X}")
        i += 1
    return " ".join(out)


def every_code_point():
    for cp in range(0x110000):
        if 0xD800 <= cp <= 0xDFFF:
            continue
        yield cp


def nfkc_tables() -> dict:
    decompositions, classes, compositions = {}, {}, []
    for cp in every_code_point():
        char = chr(cp)
        if S_BASE <= cp < S_BASE + S_COUNT:
            continue  # Hangul syllables are decomposed and composed by arithmetic, in both validators.
        full = unicodedata.normalize("NFKD", char)
        if full != char:
            decompositions[f"{cp:04X}"] = " ".join(f"{ord(c):04X}" for c in full)
        if unicodedata.combining(char):
            classes.setdefault(str(unicodedata.combining(char)), []).append(cp)
        canonical = unicodedata.decomposition(char)
        if canonical and not canonical.startswith("<"):
            parts = canonical.split()
            pair = "".join(chr(int(p, 16)) for p in parts)
            # A primary composite: two characters that NFC puts back together as this one.
            if len(parts) == 2 and unicodedata.normalize("NFC", pair) == char:
                compositions.append(f"{parts[0]} {parts[1]} {cp:04X}")
    return {
        "decompositions": decompositions,
        "combining_classes": {k: ranges(v) for k, v in sorted(classes.items(), key=lambda kv: int(kv[0]))},
        "compositions": compositions,
    }


def probe(request: dict):
    """Asks .NET, through engine_probe.cs."""
    dotnet = os.environ.get("DOTNET", str(Path.home() / ".dotnet" / "dotnet"))
    env = dict(os.environ, MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_USE_MSBUILD_SERVER="0")
    with tempfile.TemporaryDirectory() as scratch:
        asked, answered = Path(scratch) / "request.json", Path(scratch) / "answer.json"
        asked.write_text(json.dumps(request), encoding="utf-8")
        subprocess.run(
            [dotnet, "run", str(PROBE), "--", str(asked), str(answered)],
            check=True,
            env=env,
            stdout=subprocess.DEVNULL,
        )
        return json.loads(answered.read_text(encoding="utf-8"))


def write() -> None:
    tables = nfkc_tables()
    unseen = [cp for cp in every_code_point() if unicodedata.category(chr(cp)) == "Cf"]
    assigned = [cp for cp in every_code_point() if unicodedata.category(chr(cp)) != "Cn"]

    # The probe builds the API, which embeds this file, so a first run needs one to exist.
    if not OUT.exists():
        OUT.write_text("{}\n", encoding="utf-8")
    net = probe({"mode": "classes"})
    if net["space"] != net["white_space"]:
        sys.exit(".NET's \\s and char.IsWhiteSpace differ; the walk and the patterns need one list.")
    for letter, extra in net["letters"].items():
        if extra and (letter, extra) != ("k", [0x212A]):
            sys.exit(f"Under IgnoreCase .NET matches {letter!r} to {extra}; the readings handle U+212A only.")
    zeros = [cp for cp in net["digit"] if cp in net["digit"] and (cp - 1) not in net["digit"]]
    if sorted(net["digit"]) != sorted(z + i for z in zeros for i in range(10)):
        sys.exit(".NET's digits are not runs of ten from a zero.")
    spaces = set(net["space"]) | set(range(0x1C, 0x20))
    for cp in every_code_point():
        if unicodedata.category(chr(cp)) in ("Zs", "Zl", "Zp") or chr(cp).isspace():
            spaces.add(cp)

    document = {
        "_comment": COMMENT,
        "unicode_version": unicodedata.unidata_version,
        "net_runtime": net["runtime"],
        "word": ranges(net["word"]),
        "digit_zeros": " ".join(f"{z:04X}" for z in zeros),
        "net_space": ranges(net["space"]),
        "spaces": ranges(spaces),
        "unseen": ranges(unseen),
        "assigned": ranges(assigned),
        **tables,
    }
    OUT.write_text(json.dumps(document, indent=1, ensure_ascii=True) + "\n", encoding="utf-8")
    print(
        f"wrote {OUT.relative_to(HERE.parent)}: Unicode {unicodedata.unidata_version}, .NET {net['runtime']}"
    )


def check() -> None:
    sys.path.insert(0, str(HERE))
    import checks

    failures = 0
    points = list(every_code_point())
    characters = [chr(cp) for cp in points]
    answered = probe({"mode": "read", "texts": characters})
    for cp, char, net in zip(points, characters, answered, strict=True):
        ours = checks.read(char)
        if ours != net:
            failures += 1
            if failures <= 20:
                print(f"U+{cp:04X}: Python reads {ours!r}, .NET reads {net!r}")
    print(f"read: {len(points)} code points compared, {failures} differ")

    # Every UTF-16 code unit a reading can hold. U+212A is not one: NFKC makes it K in the text as read,
    # and DraftReading.Written and checks.readings make it K in the text as written, because .NET's
    # IgnoreCase takes it for k and Python's ASCII folding does not. Lone surrogates come last, made by
    # the probe, since JSON cannot carry them.
    bmp = [chr(cp) for cp in range(0x10000) if not 0xD800 <= cp <= 0xDFFF and cp != 0x212A]
    pieces = checks.single_character_pieces()
    patterns = [
        {"name": f"{name}/{case}", "pattern": piece, "ignore_case": case == "i"}
        for name, piece in pieces.items()
        for case in ("i", "-")
    ]
    net = probe({"mode": "single", "characters": bmp, "surrogates": True, "patterns": patterns})
    bmp += [chr(cp) for cp in range(0xD800, 0xE000)]
    for p in patterns:
        flags = checks.FLAGS_IGNORE_CASE if p["ignore_case"] else checks.FLAGS
        compiled = checks.re.compile("^(?:" + p["pattern"] + ")$", flags)
        ours = [i for i, c in enumerate(bmp) if compiled.match(c)]
        if ours != net[p["name"]]:
            failures += 1
            only_py = sorted(set(ours) - set(net[p["name"]]))[:5]
            only_net = sorted(set(net[p["name"]]) - set(ours))[:5]
            print(
                f"{p['name']}: Python only {[hex(ord(bmp[i])) for i in only_py]}, .NET only "
                f"{[hex(ord(bmp[i])) for i in only_net]}"
            )
    print(f"classes: {len(patterns)} pieces over {len(bmp)} code units compared, {failures} differ")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="compare both validators on every code point")
    if parser.parse_args().check:
        check()
    else:
        write()
