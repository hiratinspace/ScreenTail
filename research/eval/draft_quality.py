"""Draft quality for a corpus of sessions (ST-062).

ST-062's acceptance criteria are three numbers: an edit ratio under 25% on the cloud baseline, a
hallucination count, and a regression gate that fails a prompt change which makes either worse by more
than 10%. A number needs a definition, and these are the ones this repository uses.

**Edit ratio is words changed over words drafted.** Word level rather than step level: rewriting three
words of a twelve-word step is not the same amount of work as rewriting the step, and a ratio that
treats them alike cannot hold a 25% target. Case, punctuation and whitespace are folded before the
comparison, for the same reason `wer.py` folds them: a full stop the technician added is not an edit
worth counting against a model.

**A hallucination is a step whose evidence does not exist.** Not "a step that reads oddly" — that is the
rubric's job, and a human's. A step carries `frame_refs` and `transcript_refs`; when one of those names
a frame or a segment the session does not contain, the model invented the thing it claims to have seen.
That is countable, and it is the failure the whole review flow exists to catch.

**A step with no references at all is unsupported, not invented.** Narration without a screenshot is
ordinary (ST-076), and counting it as an invention would make the number meaningless on real sessions.
It is reported separately so a prompt that stops citing anything is still visible.

The corpus of real sessions is ST-030's and does not exist yet. This runs over whatever directory it is
given, so the day there are ten labelled sessions the only new thing is the data. A session directory
holds `session.json`; a reference note, when someone has written one, lives beside it as
`reference.json` with the same shape as the draft plus an optional `rubric` object of human scores.

Usage:
    python research/eval/draft_quality.py research/fixtures/handcrafted
    python research/eval/draft_quality.py <corpus> --json
    python research/eval/draft_quality.py <corpus> --max-edit-ratio 0.25
    python research/eval/draft_quality.py <corpus> --baseline baseline.json
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

# ST-062 AC3: the cloud baseline's target. Named once, here, so a change to it is a change to this file.
TARGET_EDIT_RATIO = 0.25

# ST-062 AC2: more than this much worse than the baseline fails the gate.
MAX_REGRESSION = 0.10

_PUNCTUATION = re.compile(r"[^\w\s]", flags=re.UNICODE)


def normalise(text: str) -> list[str]:
    """Words, folded the way `wer.py` folds them: case, punctuation and whitespace are not edits."""
    return _PUNCTUATION.sub(" ", text.casefold()).split()


@dataclass(frozen=True)
class Score:
    """One session's numbers."""

    words_drafted: int
    words_changed: int
    steps_drafted: int
    steps_changed: int
    steps_removed: int
    steps_added: int
    hallucinated: int
    hallucinated_steps: list[int]
    unsupported: int
    rubric: dict[str, int] = field(default_factory=dict)

    @property
    def edit_ratio(self) -> float:
        return 0.0 if self.words_drafted == 0 else self.words_changed / self.words_drafted


@dataclass(frozen=True)
class Corpus:
    """What a run reports, and what a baseline file holds."""

    edit_ratio: float
    hallucinated: int
    sessions: int
    unsupported: int = 0

    def as_json(self) -> dict[str, object]:
        return {
            "sessions": self.sessions,
            "edit_ratio": round(self.edit_ratio, 4),
            "hallucinated": self.hallucinated,
            "unsupported": self.unsupported,
        }


def _steps(note: dict) -> list[str]:
    return [str(step.get("text", "")) for step in note.get("steps", [])]


def _distance(before: list[str], after: list[str]) -> int:
    """Levenshtein over words: substitutions, deletions and insertions, the same count `wer.py` uses."""
    if not before:
        return len(after)
    previous = list(range(len(before) + 1))
    for j, word in enumerate(after, start=1):
        current = [j]
        for i, original in enumerate(before, start=1):
            current.append(
                previous[i - 1] if original == word else 1 + min(previous[i - 1], previous[i], current[i - 1])
            )
        previous = current
    return previous[-1]


def score(draft: dict, published: dict, session: dict | None = None) -> Score:
    """One session: how much of the draft was rewritten, and how much of it was invented."""
    drafted = _steps(draft)
    final = _steps(published)

    words_drafted = sum(len(normalise(text)) for text in drafted)
    words_changed = _distance(
        [word for text in drafted for word in normalise(text)],
        [word for text in final for word in normalise(text)],
    )

    pairs = min(len(drafted), len(final))
    steps_changed = sum(1 for i in range(pairs) if normalise(drafted[i]) != normalise(final[i]))
    steps_removed = max(0, len(drafted) - len(final))
    steps_added = max(0, len(final) - len(drafted))

    hallucinated_steps: list[int] = []
    unsupported = 0
    if session is not None:
        frames = {str(frame.get("id")) for frame in session.get("frames", [])}
        segments = {str(segment.get("id")) for segment in session.get("transcript", [])}
        for number, step in enumerate(draft.get("steps", []), start=1):
            cited = [str(ref) for ref in step.get("frame_refs", [])] + [
                str(ref) for ref in step.get("transcript_refs", [])
            ]
            if not cited:
                unsupported += 1
            elif any(ref not in frames and ref not in segments for ref in cited):
                hallucinated_steps.append(number)

    return Score(
        words_drafted=words_drafted,
        words_changed=words_changed,
        steps_drafted=len(drafted),
        steps_changed=steps_changed + steps_removed + steps_added,
        steps_removed=steps_removed,
        steps_added=steps_added,
        hallucinated=len(hallucinated_steps),
        hallucinated_steps=hallucinated_steps,
        unsupported=unsupported,
        rubric={str(k): int(v) for k, v in (published.get("rubric") or {}).items()},
    )


def regressed(baseline: Corpus, current: Corpus) -> str | None:
    """Why this run fails against the baseline, or None. AC2's gate: worse by more than 10%."""
    allowed = baseline.edit_ratio * (1 + MAX_REGRESSION)
    if current.edit_ratio > allowed + 1e-9:
        return (
            f"edit ratio {current.edit_ratio:.1%} is more than {MAX_REGRESSION:.0%} worse than the "
            f"baseline's {baseline.edit_ratio:.1%}"
        )
    if current.hallucinated > baseline.hallucinated:
        return (
            f"hallucinated steps went from {baseline.hallucinated} to {current.hallucinated}; "
            "an invented step is never within budget"
        )
    return None


def run(corpus: Path) -> tuple[Corpus, list[tuple[str, Score]]]:
    """Every session directory under `corpus`. A session with no reference note is scored against itself."""
    scores: list[tuple[str, Score]] = []
    for session_file in sorted(corpus.rglob("session.json")):
        session = json.loads(session_file.read_text(encoding="utf-8"))
        draft = session.get("draft")
        if not draft:
            continue
        reference_file = session_file.with_name("reference.json")
        reference = (
            json.loads(reference_file.read_text(encoding="utf-8")) if reference_file.exists() else draft
        )
        scores.append((session_file.parent.name, score(draft, reference, session=session)))

    words = sum(s.words_drafted for _, s in scores)
    changed = sum(s.words_changed for _, s in scores)
    return (
        Corpus(
            edit_ratio=0.0 if words == 0 else changed / words,
            hallucinated=sum(s.hallucinated for _, s in scores),
            sessions=len(scores),
            unsupported=sum(s.unsupported for _, s in scores),
        ),
        scores,
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Draft quality over a corpus of sessions (ST-062).")
    parser.add_argument("corpus", type=Path, help="A directory of session folders, each with session.json")
    parser.add_argument("--json", action="store_true", help="Machine-readable report on stdout")
    parser.add_argument(
        "--max-edit-ratio",
        type=float,
        default=None,
        help=f"Exit 1 when the corpus is above this (the target is {TARGET_EDIT_RATIO:.0%})",
    )
    parser.add_argument(
        "--baseline", type=Path, default=None, help="A previous report, for the regression gate"
    )
    arguments = parser.parse_args(argv)

    if not arguments.corpus.is_dir():
        print(f"No such corpus: {arguments.corpus}", file=sys.stderr)
        return 2

    corpus, scores = run(arguments.corpus)
    if arguments.json:
        print(
            json.dumps(
                corpus.as_json() | {"by_session": {name: s.edit_ratio for name, s in scores}}, indent=2
            )
        )
    else:
        print(
            f"{corpus.sessions} session(s): edit ratio {corpus.edit_ratio:.1%}, "
            f"{corpus.hallucinated} hallucinated, {corpus.unsupported} unsupported"
        )
        for name, session_score in scores:
            print(
                f"  {name}: {session_score.edit_ratio:.1%} over {session_score.words_drafted} words, "
                f"{session_score.hallucinated} hallucinated"
            )

    failed = False
    if arguments.max_edit_ratio is not None and corpus.edit_ratio > arguments.max_edit_ratio:
        print(f"Edit ratio {corpus.edit_ratio:.1%} is above the budget of {arguments.max_edit_ratio:.1%}.")
        failed = True

    if arguments.baseline is not None and arguments.baseline.exists():
        previous = json.loads(arguments.baseline.read_text(encoding="utf-8"))
        baseline = Corpus(
            edit_ratio=float(previous["edit_ratio"]),
            hallucinated=int(previous["hallucinated"]),
            sessions=int(previous["sessions"]),
        )
        if (reason := regressed(baseline, corpus)) is not None:
            print(f"Regression: {reason}")
            failed = True

    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
