"""ST-031: the parts of the real-speech measurement that decide what its numbers mean.

The measurement itself needs a corpus, a model and several minutes, so it is run by hand
(research/eval/real_speech/measure.py). What is tested here is the reference it scores against and the
table it prints: a reference that tidied up what was said, or a total that pooled read-aloud speech with
people talking, would report a better number than the truth.
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "eval" / "real_speech"))

import measure  # noqa: E402

WORDS = """<?xml version="1.0" encoding="ISO-8859-1" standalone="yes"?>
<nite:root nite:id="X.B.words" xmlns:nite="http://nite.sourceforge.net/">
   <vocalsound nite:id="X.B.words0" starttime="0.0" endtime="1.5" type="laugh"/>
   <w nite:id="X.B.words3" starttime="12.1" endtime="12.3">we&#39;re</w>
   <w nite:id="X.B.words1" starttime="10.9" endtime="11.0">Um</w>
   <disfmarker nite:id="X.B.words2" starttime="12.1" endtime="12.1"/>
   <w nite:id="X.B.words4" starttime="12.3" endtime="12.4">fine</w>
   <w nite:id="X.B.words5" starttime="12.4" endtime="12.4" punc="true">.</w>
</nite:root>
"""


def test_the_reference_is_what_was_said_in_the_order_it_was_said():
    assert measure.ami_reference(WORDS) == "Um we're fine\n"


def test_the_reference_keeps_hesitations_and_drops_only_punctuation_and_noises():
    said = measure.ami_reference(WORDS).split()

    assert "Um" in said
    assert "." not in said
    assert "laugh" not in said


def _row(corpus: str, words: int, errors_full: int, errors_sized: int) -> dict:
    def arm(errors: int) -> dict:
        return {
            "wer": errors / words,
            "reference_words": words,
            "substitutions": errors,
            "deletions": 0,
            "insertions": 0,
        }

    return {
        "recording": f"{corpus} one",
        "corpus": corpus,
        "audio_seconds": 600.0,
        "full": arm(errors_full),
        "sized": arm(errors_sized),
        "sized_against_full": {"wer": 0.0},
    }


def test_totals_are_per_corpus_and_never_pooled():
    table = measure.table([_row("AMI", 100, 30, 40), _row("LibriSpeech", 900, 9, 9)])

    assert "AMI, full: 30.0% over 100 reference words" in table
    assert "AMI, sized: 40.0% over 100 reference words" in table
    assert "LibriSpeech, full: 1.0% over 900 reference words" in table
    assert "1000 reference words" not in table


def test_the_script_refuses_a_work_folder_inside_the_checkout(tmp_path):
    inside = measure.REPO / "research" / "eval" / "real_speech" / "corpus-not-ignored"

    try:
        measure.main(["--work", str(inside)])
    except SystemExit as stopped:
        assert "not ignored" in str(stopped)
    else:
        raise AssertionError("a work folder inside the repository was accepted")
    assert not inside.exists()
