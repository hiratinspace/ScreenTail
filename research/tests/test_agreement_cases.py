"""Every case agreement.py pinned, read by checks.py as Unicode defines the reading (ST-061, PR #206).

checks.read and the backend's DraftReading are two implementations of NFKC, and differential-cases.json
held them to about 9,600 sentences. `prompts/agreement-cases.json.gz` pins every scalar value, every pair
and triple the tables compose or reorder, Hangul in each shape, long runs of marks, 112,654 seeded random
sequences, and text either side of the reading's bound, as digests of what Python's own NFKC reads them as.
The backend's AgreementCasesTests holds DraftReading to the same digests, so the two agree on every case.

And the reading is bounded: a device may send 600,000 characters of transcript, NFKC makes U+FDFA eighteen,
and a text whose decomposition grows past the bound is refused rather than read.
"""

from __future__ import annotations

import copy
import json
import sys
import time
import unicodedata
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "prompts"))

import agreement  # noqa: E402
import checks  # noqa: E402

FILE = agreement.load()
FIXTURE = ROOT / "fixtures" / "handcrafted" / "spooler-stopped-screenconnect" / "session.json"

# The most transcript the backend's BundleLimits accepts (MaxTotalTextChars, ids included) and the most one
# segment may carry (MaxTextChars).
MAX_TOTAL_TEXT_CHARS = 600_000
MAX_TEXT_CHARS = 10_000

# Measured on the development machine (an M-series Mac, CPython 3.12), each including building the session:
# U+FDFA 0.05 s, U+3392 0.50 s, U+FB4F 0.43 s, the run of marks 0.27 s. Before the bound, U+FDFA took 2.8 s
# and was read rather than refused, which its verdict catches, and the run of marks took 60 s, which this
# budget catches with room left for a slower runner.
BUDGET_SECONDS = 5.0


def cases_of(group: str) -> list[str]:
    return list(agreement.scalars()) if group == "scalars" else FILE["groups"][group]["cases"]


@pytest.mark.parametrize("group", list(FILE["groups"]))
def test_every_case_reads_as_unicode_defines_it(group: str):
    cases = cases_of(group)
    ours = agreement.digests(map(checks.read, cases))
    pinned = FILE["groups"][group]["digests"]
    assert len(ours) == len(pinned)
    wrong = [i for i, (a, b) in enumerate(zip(ours, pinned, strict=True)) if a != b]
    assert not wrong, (
        f"{len(wrong)} of {len(pinned)} digests differ, the first covering "
        f"{cases[wrong[0] * agreement.CHUNK][:40]!r} onwards; prompts/agreement.py --check names them"
    )


def test_the_file_is_what_agreement_py_writes_from_these_tables():
    # The cases are drawn from unicode-tables.json, so new tables need new cases, and the bound is
    # shared-rules.json's.
    assert FILE["read_growth"] == int(checks.RULES["read_growth"])
    assert FILE["read_slack"] == int(checks.RULES["read_slack"])
    assert FILE["unicode_version"] == checks.TABLES["unicode_version"]
    listed = {name: group["cases"] for name, group in FILE["groups"].items() if "cases" in group}
    assert listed == agreement.corpus(), "run prompts/agreement.py again"
    assert len(listed["random"]) >= 100_000


def test_the_tables_nfkc_is_pythons_on_every_case_this_python_knows():
    # The digests are Python's NFKC at the tables' version. CI's Python may have another, and NFKC is
    # stable for every character both versions assign, so the comparison is over those.
    assigned = set(checks.code_points(checks.TABLES["assigned"]))

    def known(text: str) -> bool:
        return all(ord(c) in assigned and unicodedata.category(c) != "Cn" for c in text)

    wrong = []
    for group in FILE["groups"]:
        for case in cases_of(group):
            if not known(case) or checks.read(case) is None:
                continue
            ours = "".join(map(chr, checks.nfkc(checks.scalar_values(case))))
            if ours != unicodedata.normalize("NFKC", case):
                wrong.append(f"{group}: {case[:20]!r}")
    assert not wrong, f"{len(wrong)} cases normalise differently, such as {wrong[:5]}"


SESSION = json.loads(FIXTURE.read_text(encoding="utf-8"))


def largest(unit: str) -> dict:
    """The session with the most transcript the backend accepts, all of one character, and a draft that
    quotes it and has a result of it."""
    session = copy.deepcopy(SESSION)
    transcript = []
    for i in range(1, MAX_TOTAL_TEXT_CHARS // MAX_TEXT_CHARS + 1):
        segment_id = f"t-{i:04d}"
        text = (unit * MAX_TEXT_CHARS)[: MAX_TEXT_CHARS - len(segment_id)]
        transcript.append({"id": segment_id, "ts_ms": i * 1000, "text": text})
    session["transcript"] = transcript
    assert sum(len(t["id"]) + len(t["text"]) for t in transcript) == MAX_TOTAL_TEXT_CHARS
    draft = session["draft"]
    draft["steps"] = [
        {
            "text": f"The customer said \u201c{(unit * 4)[:4]}\u201d.",
            "confidence": "high",
            "frame_refs": [],
            "transcript_refs": ["t-0001"],
        }
    ]
    draft["result"] = (unit * 32_768)[:32_768]
    return session


@pytest.mark.parametrize(
    "unit,refused",
    [("\ufdfa", True), ("\u3392", False), ("\ufb4f", False), ("\u0301\u0323", False)],
    ids=["U+FDFA", "U+3392", "U+FB4F", "marks"],
)
def test_the_largest_bundle_of_one_character_is_checked_within_budget(unit: str, refused: bool):
    session = largest(unit)
    started = time.perf_counter()
    errors = checks.check_draft(session["draft"], session)
    elapsed = time.perf_counter() - started
    assert bool(errors) == refused, errors
    assert elapsed < BUDGET_SECONDS, f"checking took {elapsed:.1f} s"


def test_a_text_that_grows_past_the_bound_is_refused_rather_than_read():
    session = largest("\ufdfa")
    errors = checks.check_draft(session["draft"], session)
    assert any(e.startswith("result: ") and "too long" in e for e in errors), errors
    assert any(e.startswith("Step 1 quotes") for e in errors), errors
    assert checks.read("\ufdfa" * 5 + "aa") is not None
    assert checks.read("\ufdfa" * 7 + "a" * 11) is None
