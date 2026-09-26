"""ST-062: the draft-quality harness, which is how "edit ratio < 25%" and "no hallucinated steps" become
numbers a prompt change is measured against.

The corpus of real sessions is ST-030's and does not exist yet, so the harness is built and tested first,
the way `wer.py` was built before there was audio: the scoring has to be right and arguable-about now, so
that the day there are ten labelled sessions the only new thing is the data.

Most of these tests are about what counts as a hallucination and what counts as an edit, because that is
where a quality harness quietly measures the wrong thing.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "eval"))

import draft_quality  # noqa: E402


def test_a_note_published_unchanged_scores_zero():
    draft = {"steps": [{"text": "Restarted the spooler."}, {"text": "Cleared the queue."}]}
    result = draft_quality.score(draft, draft)

    assert result.edit_ratio == 0.0
    assert result.steps_changed == 0
    assert result.hallucinated == 0


def test_the_edit_ratio_is_words_changed_over_words_drafted():
    # Word level rather than step level: rewriting three words of a twelve-word step is not the same
    # amount of work as rewriting the step, and a ratio that says it is cannot hold a 25% target.
    draft = {"steps": [{"text": "Restart the print spooler service"}]}
    published = {"steps": [{"text": "Restarted the print spooler service"}]}

    result = draft_quality.score(draft, published)

    assert result.edit_ratio == pytest.approx(1 / 5)
    assert result.steps_changed == 1


def test_a_step_the_technician_deleted_counts_as_edited():
    draft = {"steps": [{"text": "Restarted the spooler"}, {"text": "Checked the toner level"}]}
    published = {"steps": [{"text": "Restarted the spooler"}]}

    result = draft_quality.score(draft, published)

    assert result.steps_removed == 1
    assert result.edit_ratio > 0


def test_a_step_with_no_evidence_is_a_hallucination():
    # The definition that matters: a step is grounded when its frame or transcript references exist in
    # the session. A model that invents "Rebooted the server" with no frame behind it is the failure
    # mode the whole review flow exists to catch, and it has to be counted rather than eyeballed.
    session = {
        "frames": [{"id": "f-0001"}],
        "transcript": [{"id": "t-0001"}],
    }
    draft = {
        "steps": [
            {"text": "Found the spooler stopped.", "frame_refs": ["f-0001"]},
            {"text": "Rebooted the domain controller.", "frame_refs": ["f-0404"]},
            {"text": "Told them it was fixed.", "transcript_refs": ["t-0404"]},
        ]
    }

    result = draft_quality.score(draft, draft, session=session)

    assert result.hallucinated == 2
    assert result.hallucinated_steps == [2, 3]


def test_a_step_with_no_references_at_all_is_unsupported_not_hallucinated():
    # Narration without a screenshot is ordinary (ST-076) and the aligner says so. Counting it as an
    # invention would make the number meaningless on every real session.
    session = {"frames": [{"id": "f-0001"}], "transcript": [{"id": "t-0001"}]}
    draft = {"steps": [{"text": "Explained what had happened."}]}

    result = draft_quality.score(draft, draft, session=session)

    assert result.hallucinated == 0
    assert result.unsupported == 1


def test_the_rubric_scores_come_from_the_reference_when_there_is_one():
    reference = {
        "steps": [{"text": "Found the Print Spooler service stopped."}],
        "result": "Printing works again.",
        "rubric": {"accuracy": 5, "completeness": 4, "tone": 5},
    }
    draft = {
        "steps": [{"text": "Found the print spooler service stopped"}],
        "result": "Printing works again.",
    }

    result = draft_quality.score(draft, reference)

    assert result.rubric == {"accuracy": 5, "completeness": 4, "tone": 5}
    assert result.edit_ratio == pytest.approx(0.0), "case and punctuation are not edits"


def test_the_gate_fails_on_a_regression_beyond_ten_percent():
    # AC2: prompt change -> CI reports edit distance and hallucination count; >10% regression fails.
    baseline = draft_quality.Corpus(edit_ratio=0.20, hallucinated=0, sessions=10)
    barely_worse = draft_quality.Corpus(edit_ratio=0.21, hallucinated=0, sessions=10)
    much_worse = draft_quality.Corpus(edit_ratio=0.23, hallucinated=0, sessions=10)
    one_invention = draft_quality.Corpus(edit_ratio=0.20, hallucinated=1, sessions=10)

    assert draft_quality.regressed(baseline, barely_worse) is None
    assert "edit ratio" in draft_quality.regressed(baseline, much_worse)
    assert "hallucinated" in draft_quality.regressed(baseline, one_invention)


def test_the_target_is_a_quarter_and_it_is_named_once():
    assert draft_quality.TARGET_EDIT_RATIO == 0.25
    assert draft_quality.MAX_REGRESSION == 0.10


def test_it_runs_over_a_corpus_directory_and_reports_json():
    result = subprocess.run(  # noqa: S603
        [
            sys.executable,
            str(ROOT / "eval" / "draft_quality.py"),
            str(ROOT / "fixtures" / "handcrafted"),
            "--json",
        ],
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 0, result.stderr
    report = json.loads(result.stdout)

    # Every handcrafted fixture that has a draft, and no others: the interrupted one has no draft to score.
    drafted = [
        d.name
        for d in sorted((ROOT / "fixtures" / "handcrafted").iterdir())
        if (d / "session.json").exists() and json.loads((d / "session.json").read_text()).get("draft")
    ]
    assert report["sessions"] == len(drafted)
    assert sorted(report["by_session"]) == drafted
    assert report["hallucinated"] == 0, "the hand-written fixtures cite frames they contain"
    assert report["edit_ratio"] == 0.0, "a fixture with no reference scores itself: nothing was edited"


def test_the_budget_flag_fails_when_the_corpus_is_over_it():
    result = subprocess.run(  # noqa: S603
        [
            sys.executable,
            str(ROOT / "eval" / "draft_quality.py"),
            str(ROOT / "fixtures" / "handcrafted"),
            "--max-edit-ratio",
            "-0.01",
        ],
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 1
    assert "edit ratio" in result.stdout.lower() or "edit ratio" in result.stderr.lower()
