"""ST-061: the note prompt's output contract, and the checks that enforce it.

The fixtures' drafts (ST-006) stand in for model output until ST-063 can call a real one: they are
hand-written to the prompt's rules, so if a rule and the checks disagree, these tests fail.
"""

from __future__ import annotations

import copy
import json
import sys
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parent.parent
PROMPTS = ROOT / "prompts"
HANDCRAFTED = ROOT / "fixtures" / "handcrafted"
SESSION_SCHEMA = ROOT.parent / "shared" / "schema" / "session.v1.json"

sys.path.insert(0, str(PROMPTS))
from checks import check_draft, passes_luhn, prompt_version  # noqa: E402

OUTPUT_SCHEMA = json.loads((PROMPTS / "schema.json").read_text(encoding="utf-8"))
SESSION = json.loads(SESSION_SCHEMA.read_text(encoding="utf-8"))


def bundles() -> list[Path]:
    return sorted(p for p in HANDCRAFTED.iterdir() if p.is_dir())


def sessions_with_drafts() -> list[tuple[str, dict]]:
    out = []
    for bundle in bundles():
        session = json.loads((bundle / "session.json").read_text(encoding="utf-8"))
        if "draft" in session:
            out.append((bundle.name, session))
    return out


@pytest.fixture(scope="module")
def validator() -> Draft202012Validator:
    Draft202012Validator.check_schema(OUTPUT_SCHEMA)
    return Draft202012Validator(OUTPUT_SCHEMA)


# ---- the contract itself ---------------------------------------------------------------------------


def test_output_schema_does_not_drift_from_the_stored_draft_type():
    # A note the model may return but the client cannot store is a failed draft, so the two shapes
    # have to agree. transcript_refs is the one deliberate difference: the prompt demands citations.
    draft_note = SESSION["$defs"]["DraftNote"]
    assert set(OUTPUT_SCHEMA["required"]) == set(draft_note["required"])
    assert set(OUTPUT_SCHEMA["properties"]) == set(draft_note["properties"])

    step = OUTPUT_SCHEMA["$defs"]["Step"]
    stored_step = SESSION["$defs"]["DraftStep"]
    assert set(step["properties"]) == set(stored_step["properties"])
    assert set(step["required"]) - set(stored_step["required"]) == {"transcript_refs"}

    assert OUTPUT_SCHEMA["properties"]["source"]["enum"] == SESSION["$defs"]["DraftSource"]["enum"]
    assert step["properties"]["confidence"]["enum"] == SESSION["$defs"]["StepConfidence"]["enum"]


def test_output_schema_refuses_anything_it_did_not_ask_for(validator: Draft202012Validator):
    draft = sessions_with_drafts()[0][1]["draft"]
    extra = copy.deepcopy(draft)
    extra["confidence_overall"] = 0.8
    assert not validator.is_valid(extra), "unknown keys must be rejected, not silently stored"

    missing = copy.deepcopy(draft)
    del missing["kb_reason"]
    assert not validator.is_valid(missing)


def test_the_prompt_documents_every_field_it_must_return():
    text = (PROMPTS / "note_v1.md").read_text(encoding="utf-8")
    assert prompt_version() == "note_v1"
    for field in OUTPUT_SCHEMA["required"]:
        assert field in text, f"{field} is in the schema but never mentioned in the prompt"


# ---- acceptance criteria ---------------------------------------------------------------------------

WITH_DRAFTS = sessions_with_drafts()
IDS = [name for name, _ in WITH_DRAFTS]


@pytest.mark.parametrize("name,session", WITH_DRAFTS, ids=IDS)
def test_every_fixture_draft_validates(name: str, session: dict, validator: Draft202012Validator):
    # AC1: 100% of fixture outputs validate.
    errors = sorted(validator.iter_errors(session["draft"]), key=lambda e: list(e.path))
    assert not errors, f"{name}: " + "; ".join(f"{list(e.path)}: {e.message}" for e in errors[:3])


@pytest.mark.parametrize("name,session", WITH_DRAFTS, ids=IDS)
def test_every_fixture_draft_passes_the_checks(name: str, session: dict):
    assert check_draft(session["draft"], session) == []


@pytest.mark.parametrize("name,session", WITH_DRAFTS, ids=IDS)
def test_screen_only_steps_are_low_confidence(name: str, session: dict):
    # AC2. The fixtures include one such step on purpose; if none did, this test would pass vacuously.
    for step in session["draft"]["steps"]:
        if not step.get("transcript_refs"):
            assert step["confidence"] == "low", f"{name}: {step['text']!r} cites nobody but claims high"


def test_the_set_contains_a_screen_only_step():
    assert any(
        not step.get("transcript_refs")
        for _, session in sessions_with_drafts()
        for step in session["draft"]["steps"]
    ), "no screen-only step in the fixtures, so the low-confidence rule is untested"


def test_a_step_about_a_frame_from_another_session_is_rejected():
    # AC3, the adversarial case: an unrelated frame must not be able to carry an invented step.
    name, session = sessions_with_drafts()[0]
    draft = copy.deepcopy(session["draft"])
    draft["steps"].append(
        {
            "text": "Also reset the mail profile.",
            "confidence": "low",
            "frame_refs": ["f-9999"],
            "transcript_refs": [],
        }
    )

    errors = check_draft(draft, session)

    assert any("f-9999" in e and "not in this session" in e for e in errors), errors


def test_an_invented_quotation_is_rejected():
    name, session = sessions_with_drafts()[0]
    draft = copy.deepcopy(session["draft"])
    draft["steps"][0] = dict(draft["steps"][0], text='Restarted the service, "this happens every Monday".')

    errors = check_draft(draft, session)

    assert any("nobody said" in e for e in errors), errors


def test_a_real_quotation_is_accepted():
    name, session = sessions_with_drafts()[0]
    quote = session["transcript"][0]["text"]
    draft = copy.deepcopy(session["draft"])
    draft["steps"][0] = dict(draft["steps"][0], text=f'Diagnosed it: "{quote}"')

    assert check_draft(draft, session) == []


def test_high_confidence_without_a_citation_is_rejected():
    name, session = sessions_with_drafts()[0]
    draft = copy.deepcopy(session["draft"])
    draft["steps"][0] = dict(draft["steps"][0], transcript_refs=[], confidence="high")

    errors = check_draft(draft, session)

    assert any("cannot be high confidence" in e for e in errors), errors


def test_an_excluded_frame_cannot_be_cited():
    name, session = copy.deepcopy(sessions_with_drafts()[0])
    cited = session["draft"]["steps"][0]["frame_refs"][0]
    for frame in session["frames"]:
        if frame["id"] == cited:
            frame["excluded_by_user"] = True

    errors = check_draft(session["draft"], session)

    assert any("excluded" in e for e in errors), errors


def test_a_secret_put_back_into_the_note_is_rejected():
    name, session = sessions_with_drafts()[0]
    for leak in ("Card 4111 1111 1111 1111 was declined.", "Her SSN is 123-45-6789."):
        draft = copy.deepcopy(session["draft"])
        draft["steps"][0] = dict(draft["steps"][0], text=leak)
        assert check_draft(draft, session), f"{leak!r} should not reach Review"


def test_a_time_longer_than_the_session_is_rejected():
    name, session = sessions_with_drafts()[0]
    draft = copy.deepcopy(session["draft"])
    draft["suggested_time_minutes"] = int(session["duration_ms"] / 60_000) + 30

    errors = check_draft(draft, session)

    assert any("unrounded" in e for e in errors), errors


def test_a_draft_from_another_prompt_version_is_rejected():
    name, session = sessions_with_drafts()[0]
    draft = copy.deepcopy(session["draft"])
    draft["prompt_version"] = "note_v0"

    assert any("note_v0" in e for e in check_draft(draft, session))


@pytest.mark.parametrize(
    "number,expected",
    [("4111111111111111", True), ("4111111111111112", False), ("79927398713", False)],
)
def test_luhn(number: str, expected: bool):
    assert passes_luhn(number) is expected
