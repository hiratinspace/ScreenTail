"""Writes agreement-cases.json.gz: what both draft validators must read every case as (ST-061).

checks.read and DraftReading.Read are two implementations of one reading: NFKC from unicode-tables.json
(decomposition, canonical ordering, composition with its exclusions, Hangul by arithmetic), then format
characters removed and spaces made one. differential-cases.json pins about 9,600 sentences. This pins the
places two normalisers part company, exhaustively where that is possible and by the hundred thousand where
it is not:

- "scalars": every Unicode scalar value on its own, U+0000 to U+10FFFF without the surrogates. Not listed
  in the file; both suites count through them in order.
- "pairs": every pair the tables compose, and every first of one beside a mark of every combining class.
- "triples": every composing pair with a second mark after it and before it, where the second is each mark
  that composes with the first or with the pair's composite, and a mark of every combining class. Both
  orders, so marks of different classes are reordered both ways.
- "hangul": L V, L V T, LV + T, LVT + T, and syllables and jamo with a mark between them.
- "runs": a starter and a run of 33 to 300 marks of mixed classes, and four of thousands. Canonical
  ordering sorts these, and a long run is where a sort is slow or wrong.
- "random": 120,000 seeded sequences mixing all of the above with ASCII, spaces, format characters,
  compatibility characters, and characters outside the BMP.
- "bound": text either side of the reading's bound: a reading stops, and the text is refused, once its
  decomposition is longer than read_growth code points for each of the text's and read_slack more
  (shared-rules.json). U+FDFA decomposes to 18.

What each case reads as comes from Python's unicodedata.normalize("NFKC"), not from checks.py, so the file
is the Unicode definition and both validators are held to it. That needs this Python's Unicode version to
be the tables' (15.0.0, Python 3.12), and the script stops otherwise. The readings are pinned as SHA-256
digests of 4,096 cases at a time, which is what keeps the file small; --check names the cases a digest
covers when one differs.

    .venv/bin/python prompts/agreement.py           # writes prompts/agreement-cases.json.gz
    .venv/bin/python prompts/agreement.py --check   # both validators against the file, case by case

--check needs the .NET SDK, and asks .NET through engine_probe.cs. Run this again after
unicode_tables.py, since the cases are drawn from the tables.
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import random
import struct
import sys
import unicodedata
from collections.abc import Callable, Iterable, Iterator
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "agreement-cases.json.gz"
sys.path.insert(0, str(HERE))

import checks  # noqa: E402

CHUNK = 4096
SEED = 61_206
GROWTH = int(checks.RULES["read_growth"])
SLACK = int(checks.RULES["read_slack"])

S_BASE, L_BASE, V_BASE, T_BASE = 0xAC00, 0x1100, 0x1161, 0x11A7
EXPANDERS = ["\ufdfa", "\ufdfb", "\u3392", "\ufb4f", "\u33af", "\u2474", "\u3300", "\ufb03", "\u00bd"]


def scalars() -> Iterator[str]:
    for cp in range(0x110000):
        if not 0xD800 <= cp <= 0xDFFF:
            yield chr(cp)


def corpus() -> dict[str, list[str]]:
    """Every listed case, by group, in the order the digests cover them."""
    comps = checks.COMPOSITIONS
    ccc = checks.COMBINING
    representative = {}
    for cp in sorted(ccc):
        representative.setdefault(ccc[cp], cp)
    reps = sorted(representative.values())
    seconds: dict[int, set[int]] = {}
    for first, second in comps:
        seconds.setdefault(first, set()).add(second)
    firsts = sorted(seconds)

    pairs = [chr(a) + chr(b) for a, b in sorted(comps)]
    pairs += [chr(a) + chr(m) for a in firsts for m in reps]

    triples = []
    for (first, second), composite in sorted(comps.items()):
        others = seconds.get(first, set()) | seconds.get(composite, set()) | set(reps)
        for other in sorted(others - {second}):
            triples.append(chr(first) + chr(second) + chr(other))
            triples.append(chr(first) + chr(other) + chr(second))

    leads = [chr(L_BASE + i) for i in range(19)]
    vowels = [chr(V_BASE + i) for i in range(21)]
    tails = [chr(T_BASE + i) for i in range(28)]  # T_BASE itself is not a trailing consonant.
    lv = [chr(S_BASE + i * 28) for i in range(399)]
    lvt = [chr(S_BASE + i) for i in range(11172) if i % 28]
    hangul = [lead + vowel for lead in leads for vowel in vowels]
    hangul += [lead + vowel + tail for lead in leads for vowel in vowels for tail in tails]
    hangul += [syllable + tail for syllable in lv for tail in tails]
    hangul += [syllable + "\u11a8" for syllable in lvt]
    hangul += [lead + syllable for lead in leads for syllable in lv[:21]]
    hangul += [lead + vowel + "\u0301" + "\u11a8" for lead in leads for vowel in vowels]
    hangul += [lead + "\u0301" + vowel for lead in leads for vowel in vowels]
    hangul += [syllable + "\u0301" + "\u11a8" for syllable in lv]

    rng = random.Random(SEED)
    marks = sorted(ccc)
    runs = []
    for _ in range(2000):
        starter = chr(rng.choice(firsts))
        run = "".join(chr(rng.choice(marks)) for _ in range(rng.randint(33, 300)))
        runs.append(starter + run + rng.choice(["", "a", chr(rng.choice(firsts))]))
    runs.append("a" + "\u0301\u0323" * 3000)
    runs.append("a" + "\u0323\u0301" * 3000)
    runs.append("e" + "".join(chr(m) for m in sorted(marks, key=lambda m: -ccc[m])) * 3)
    runs.append("\u1100\u1161" + "\u0301" * 5000 + "\u11a8")

    decomposable = sorted(checks.DECOMPOSITIONS)
    old_jamo = [*range(0x1100, 0x1200), *range(0xA960, 0xA980), *range(0xD7B0, 0xD800)]
    pools: list[list[int]] = [
        [ord(c) for c in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,'\""],
        firsts,
        sorted({b for _, b in comps}),
        marks,
        decomposable,
        old_jamo,
        [S_BASE + rng.randrange(11172) for _ in range(500)],
        sorted(checks.UNSEEN),
        sorted(checks.SPACES),
        [ord(c) for c in EXPANDERS],
        [0x1D15E, 0x1D15F, 0x1D165, 0x1D167, 0x1D400, 0x1F600, 0x1F12A, 0x2F800, 0x110BA, 0x11127, 0xE0020],
    ]
    randoms = []
    for _ in range(120_000):
        randoms.append("".join(chr(rng.choice(rng.choice(pools))) for _ in range(rng.randint(1, 12))))

    bound = []
    for filler in ("a", "\U0001f600", "\u0301", "\uac01"):
        for expander in ("\ufdfa", "\ufdfb", "\u3392"):
            for count in range(0, 31):
                for pad in range(0, 41, 3):
                    bound.append(filler * pad + expander * count)
                    bound.append(expander * count + " " + filler * pad)
    bound.append("\ufdfa" * 5 + "aa")  # 92 code points from 7, which is 4 * 7 + 64: read.
    bound.append("\ufdfa" * 7 + "a" * 11)  # 137 from 18, one past 4 * 18 + 64: refused.

    groups = {
        "pairs": pairs,
        "triples": triples,
        "hangul": hangul,
        "runs": runs,
        "random": randoms,
        "bound": bound,
    }
    for name, cases in groups.items():
        seen: set[str] = set()
        groups[name] = [case for case in cases if not (case in seen or seen.add(case))]
    return groups


def reference(text: str) -> str | None:
    """The reading from its definition: Python's own NFKC, the bound on its own NFKD, then the tables'
    format characters removed and spaces made one."""
    points = checks.scalar_values(text)
    whole = "".join(map(chr, points))
    if len(unicodedata.normalize("NFKD", whole)) > GROWTH * len(points) + SLACK:
        return None
    out: list[str] = []
    for char in unicodedata.normalize("NFKC", whole):
        cp = ord(char)
        if cp in checks.UNSEEN:
            continue
        if cp in checks.SPACES:
            char = " "
        if char == " " and out and out[-1] == " ":
            continue
        out.append(char)
    return "".join(out)


def encoded(reading: str | None) -> bytes:
    """One reading as the digests take it: its length in UTF-16 code units, then the units, or all ones
    for a text that is refused."""
    if reading is None:
        return b"\xff\xff\xff\xff"
    units = reading.encode("utf-16-le", "surrogatepass")
    return struct.pack("<I", len(units) // 2) + units


def digests(readings: Iterable[str | None]) -> list[str]:
    """The first 16 hex digits of the SHA-256 of each 4,096 readings in turn."""
    out, sha, count = [], hashlib.sha256(), 0
    for reading in readings:
        sha.update(encoded(reading))
        count += 1
        if count == CHUNK:
            out.append(sha.hexdigest()[:16])
            sha, count = hashlib.sha256(), 0
    if count:
        out.append(sha.hexdigest()[:16])
    return out


def load() -> dict:
    with gzip.open(OUT, "rt", encoding="utf-8") as file:
        return json.load(file)


def write() -> None:
    if unicodedata.unidata_version != checks.TABLES["unicode_version"]:
        sys.exit(
            f"This Python has Unicode {unicodedata.unidata_version} and the tables are "
            f"{checks.TABLES['unicode_version']}; run this with a Python whose versions match."
        )
    groups = corpus()
    document = {
        "_comment": [
            "What both draft validators must read every case as. Written by agreement.py; do not edit.",
            "Each digest is the first 16 hex digits of the SHA-256 of 4,096 readings in turn, each its",
            "length in UTF-16 code units (uint32, little-endian) and the units, or ffffffff when the text",
            "is refused for growing past the bound. The readings are Python's unicodedata NFKC at the",
            "tables' Unicode version, not either validator's. 'scalars' is every scalar value in order.",
        ],
        "unicode_version": unicodedata.unidata_version,
        "read_growth": GROWTH,
        "read_slack": SLACK,
        "chunk": CHUNK,
        "groups": {"scalars": {"digests": digests(map(reference, scalars()))}},
    }
    for name, cases in groups.items():
        document["groups"][name] = {"digests": digests(map(reference, cases)), "cases": cases}
    raw = json.dumps(document, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    # mtime 0, so the same corpus writes the same bytes.
    OUT.write_bytes(gzip.compress(raw, compresslevel=9, mtime=0))
    counts = ", ".join(f"{name} {len(cases)}" for name, cases in groups.items())
    print(f"wrote {OUT.relative_to(HERE.parent)}: Unicode {unicodedata.unidata_version}; {counts}")


def differing(cases: list[str], read: Callable[[str], str | None], limit: int = 10) -> list[str]:
    wrong = []
    for case in cases:
        ours, theirs = read(case), reference(case)
        if ours != theirs:
            wrong.append(f"{case[:30]!r} reads as {(ours or '')[:30]!r}, not {(theirs or '')[:30]!r}")
            if len(wrong) >= limit:
                break
    return wrong


def check() -> None:
    from unicode_tables import probe

    document = load()
    failures = 0
    texts = {
        "scalars": list(scalars()),
        **{n: g["cases"] for n, g in document["groups"].items() if "cases" in g},
    }
    everything = [text for cases in texts.values() for text in cases]
    answered = iter(probe({"mode": "read", "texts": everything}))
    for name, cases in texts.items():
        net = [next(answered) for _ in cases]
        for who, readings in (("checks.read", list(map(checks.read, cases))), ("DraftReading.Read", net)):
            pinned = document["groups"][name]["digests"]
            got = digests(readings)
            bad = [i for i, (a, b) in enumerate(zip(got, pinned, strict=True)) if a != b]
            print(f"{name}: {who} {len(cases)} cases, {len(bad)} of {len(pinned)} digests differ")
            failures += len(bad)
            for index in bad[:3]:
                chunk = cases[index * CHUNK : (index + 1) * CHUNK]
                by = dict(zip(cases, readings, strict=True))
                for line in differing(chunk, by.__getitem__, 5):
                    print(f"  {line}")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="both validators against the file, case by case")
    if parser.parse_args().check:
        check()
    else:
        write()
