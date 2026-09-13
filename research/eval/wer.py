"""Word error rate for the transcript pipeline (ST-027).

ST-027's acceptance criterion is "10-minute narrated test -> WER <= 15% with the default model", which
needs a number, and a number needs a definition everyone agrees on. This is the standard one:

    WER = (substitutions + deletions + insertions) / words in the reference

computed from the Levenshtein alignment between the reference words and the words the engine produced.

**Normalisation is part of the definition, not a convenience.** A transcript of a support call is full of
things that are the same word written differently: "Wi-Fi" and "wifi", "OK" and "okay", a full stop the
engine did or did not put in. Scoring those as errors would measure punctuation rather than recognition,
and would make the 15% target say nothing about whether the note can be written from the transcript. So
the comparison is case-insensitive, closes up hyphens, drops the rest of the punctuation including
apostrophes, and collapses whitespace. It deliberately does not expand numbers or spell out abbreviations:
"2" and "two" really are different, and a technician reading back a serial number is exactly where that
difference matters.

Usage:
    python research/eval/wer.py reference.txt hypothesis.txt
    python research/eval/wer.py --json reference.txt hypothesis.txt
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import unicodedata
from dataclasses import dataclass
from pathlib import Path

# Kept out of the class so a caller can normalise a corpus the same way before doing anything else.
_PUNCTUATION = re.compile(r"[^\w\s]", flags=re.UNICODE)
# Closed up rather than spaced out: replacing these with a space would split "it's" into two words and
# score an engine that heard it perfectly as both a substitution and an insertion.
_ELIDED = re.compile(r"[-\u2010-\u2015'\u2018\u2019\u02bc]", flags=re.UNICODE)
_WHITESPACE = re.compile(r"\s+", flags=re.UNICODE)


def normalise(text: str) -> list[str]:
    """Turns a transcript into the words WER is counted over."""
    folded = unicodedata.normalize("NFKC", text).casefold()

    # Hyphens and apostrophes are closed up, so "Wi-Fi" and "wifi", "didn't" and "didnt", "it's" and
    # "its" are all the same word — which they are, for a note written from this transcript. Two costs,
    # both accepted: "twenty-five" becomes "twentyfive" and will not match "twenty five", and a
    # contraction is conflated with a possessive. Both are rare in a support call; "Wi-Fi" is in nearly
    # every one of them, and the alternative charges 50% WER on a two-word sentence heard perfectly.
    joined = _ELIDED.sub("", folded)

    # Everything else is punctuation the engine had no way to know we wanted, and becomes a break.
    stripped = _PUNCTUATION.sub(" ", joined)
    return [w for w in _WHITESPACE.split(stripped) if w]


@dataclass(frozen=True)
class WerResult:
    """The number, and enough of the working to argue with it."""

    reference_words: int
    substitutions: int
    deletions: int
    insertions: int

    @property
    def errors(self) -> int:
        return self.substitutions + self.deletions + self.insertions

    @property
    def wer(self) -> float:
        # An empty reference with any output is entirely wrong; two empties are entirely right. Returning
        # zero for the first would let an engine that emits noise onto silence score perfectly.
        if self.reference_words == 0:
            return 0.0 if self.errors == 0 else 1.0
        return self.errors / self.reference_words

    def as_dict(self) -> dict[str, float | int]:
        return {
            "wer": round(self.wer, 4),
            "reference_words": self.reference_words,
            "substitutions": self.substitutions,
            "deletions": self.deletions,
            "insertions": self.insertions,
        }

    def __str__(self) -> str:
        return (
            f"WER {self.wer:.1%} over {self.reference_words} reference words "
            f"({self.substitutions} substituted, {self.deletions} deleted, {self.insertions} inserted)"
        )


def measure(reference: str, hypothesis: str) -> WerResult:
    """Aligns the two transcripts and counts what it took to turn one into the other."""
    ref = normalise(reference)
    hyp = normalise(hypothesis)
    return _align(ref, hyp)


def _align(ref: list[str], hyp: list[str]) -> WerResult:
    """
    Levenshtein over words, carrying the edit counts rather than only the distance.

    Two rows rather than the full matrix: a ten-minute narration is a few thousand words, and the full
    table would be tens of millions of cells for a number that only needs the previous row.
    """
    # Each cell is (cost, substitutions, deletions, insertions).
    previous: list[tuple[int, int, int, int]] = [(j, 0, 0, j) for j in range(len(hyp) + 1)]

    for i, ref_word in enumerate(ref, start=1):
        current: list[tuple[int, int, int, int]] = [(i, 0, i, 0)]
        for j, hyp_word in enumerate(hyp, start=1):
            if ref_word == hyp_word:
                current.append(previous[j - 1])
                continue

            sub_cost, sub_s, sub_d, sub_i = previous[j - 1]
            del_cost, del_s, del_d, del_i = previous[j]
            ins_cost, ins_s, ins_d, ins_i = current[j - 1]

            best = min(sub_cost, del_cost, ins_cost)
            if best == sub_cost:
                current.append((best + 1, sub_s + 1, sub_d, sub_i))
            elif best == del_cost:
                current.append((best + 1, del_s, del_d + 1, del_i))
            else:
                current.append((best + 1, ins_s, ins_d, ins_i + 1))

        previous = current

    _, substitutions, deletions, insertions = previous[-1]
    return WerResult(len(ref), substitutions, deletions, insertions)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Word error rate between a reference and a transcript.")
    parser.add_argument("reference", type=Path, help="What was actually said.")
    parser.add_argument("hypothesis", type=Path, help="What the engine produced.")
    parser.add_argument("--json", action="store_true", help="Machine-readable output.")
    parser.add_argument(
        "--budget",
        type=float,
        default=None,
        help="Fail with exit code 1 when the WER is above this (ST-027 budgets 0.15).",
    )
    args = parser.parse_args(argv)

    result = measure(args.reference.read_text(encoding="utf-8"), args.hypothesis.read_text(encoding="utf-8"))
    print(json.dumps(result.as_dict(), indent=2) if args.json else result)

    if args.budget is not None and result.wer > args.budget:
        print(f"WER {result.wer:.1%} is above the {args.budget:.0%} budget.", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
