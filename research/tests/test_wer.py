"""ST-027: the word error rate script, which is how "WER <= 15%" becomes a number anyone can check.

The acceptance criterion is a threshold, so the thing computing it has to be right about the easy cases
and explicit about the arguable ones. Most of these tests are about normalisation rather than about edit
distance, because that is where a WER script quietly measures the wrong thing: score punctuation as errors
and the number stops saying anything about whether a note can be written from the transcript.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "eval"))

import wer  # noqa: E402


def test_an_exact_transcript_scores_zero():
    result = wer.measure("the printer is offline", "the printer is offline")

    assert result.wer == 0.0
    assert result.errors == 0


def test_each_kind_of_error_is_counted_separately():
    # A single number cannot be argued with. Knowing it was three deletions rather than three
    # substitutions is the difference between "the engine is mishearing" and "the engine is not hearing".
    result = wer.measure("the printer is offline again", "the printer was offline")

    assert result.substitutions == 1  # is -> was
    assert result.deletions == 1  # again
    assert result.insertions == 0
    assert result.reference_words == 5
    assert result.wer == pytest.approx(0.4)


def test_insertions_count_against_the_reference_length():
    # WER can exceed 100%: an engine that emits noise onto a short reference is worse than one that emits
    # nothing. A definition that capped it at 1.0 would hide exactly that failure.
    result = wer.measure("hello", "hello there my friend indeed")

    assert result.insertions == 4
    assert result.wer == pytest.approx(4.0)


@pytest.mark.parametrize(
    ("reference", "hypothesis"),
    [
        ("The printer is offline.", "the printer is offline"),
        ("Wi-Fi is down", "wifi is down"),
        ("Restart   the  spooler", "Restart the spooler"),
        ("It's the spooler, again!", "its the spooler again"),
        ("PRINTER OFFLINE", "printer offline"),
    ],
)
def test_writing_the_same_words_differently_is_not_an_error(reference: str, hypothesis: str):
    # A transcript is scored to find out whether the note can be written from it, not whether the engine
    # punctuates. Counting these would measure the wrong thing and make the 15% budget meaningless.
    assert wer.measure(reference, hypothesis).wer == 0.0


@pytest.mark.parametrize(
    ("reference", "hypothesis"),
    [
        ("serial 2", "serial two"),
        ("port 443", "port four four three"),
        ("call the MSP", "call the managed service provider"),
    ],
)
def test_saying_the_same_thing_a_different_way_is_an_error(reference: str, hypothesis: str):
    # Deliberately not normalised away. A technician reading back a serial number or a port is exactly
    # where "2" and "two" are different, and an engine that gets them wrong should score for it.
    assert wer.measure(reference, hypothesis).wer > 0


def test_an_empty_transcript_of_a_real_narration_is_entirely_wrong():
    # The failure mode worth catching: the mic was muted, or VAD swallowed everything, and the pipeline
    # produced nothing. That is 100% wrong, not 0% wrong.
    assert wer.measure("the printer is offline", "").wer == 1.0


def test_silence_transcribed_as_silence_is_right():
    assert wer.measure("", "").wer == 0.0


def test_noise_on_silence_is_entirely_wrong():
    # Nothing was said and the engine produced words. Dividing by a zero-length reference would be
    # undefined, and returning zero would score hallucination as perfect.
    assert wer.measure("", "the printer is offline").wer == 1.0


def test_a_long_narration_is_scored_without_building_the_whole_matrix():
    # Ten minutes of speech is a few thousand words; the full Levenshtein table would be tens of millions
    # of cells. This is here so a rewrite that materialises it fails on time rather than in production.
    reference = " ".join(f"word{i}" for i in range(4_000))
    hypothesis = " ".join(f"word{i}" for i in range(4_000) if i % 50)

    result = wer.measure(reference, hypothesis)

    assert result.deletions == 80
    assert result.wer == pytest.approx(0.02)


def test_the_summary_says_enough_to_argue_with():
    result = wer.measure("the printer is offline", "the printer was offline")

    assert "WER 25.0%" in str(result)
    assert "4 reference words" in str(result)
    assert result.as_dict()["substitutions"] == 1


def test_the_command_line_reports_and_honours_a_budget(tmp_path: Path):
    # The form CI will use: a number on stdout and an exit code that means something.
    reference = tmp_path / "reference.txt"
    hypothesis = tmp_path / "hypothesis.txt"
    reference.write_text("the printer is offline", encoding="utf-8")
    hypothesis.write_text("the printer was offline", encoding="utf-8")

    inside = subprocess.run(  # noqa: S603
        [sys.executable, str(ROOT / "eval" / "wer.py"), str(reference), str(hypothesis), "--budget", "0.5"],
        capture_output=True,
        text=True,
        check=False,
    )
    script = str(ROOT / "eval" / "wer.py")
    outside = subprocess.run(  # noqa: S603
        [sys.executable, script, str(reference), str(hypothesis), "--json", "--budget", "0.15"],
        capture_output=True,
        text=True,
        check=False,
    )

    assert inside.returncode == 0, inside.stderr
    assert "WER 25.0%" in inside.stdout
    assert outside.returncode == 1
    assert '"wer": 0.25' in outside.stdout
    assert "above the 15% budget" in outside.stderr
