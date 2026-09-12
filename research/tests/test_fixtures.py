"""ST-006: the hand-crafted bundles and the redaction seed must stay loadable and honest.

These fixtures are what Review, the prompt work and the redaction evaluation are built against before
the capture engine exists, so a fixture that drifts from the schema is a broken tool, not a stale file.
"""

from __future__ import annotations

import json
import struct
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parent.parent
FIXTURES = ROOT / "fixtures"
HANDCRAFTED = FIXTURES / "handcrafted"
SEED = FIXTURES / "redaction-seed"
SCHEMA = ROOT.parent / "shared" / "schema" / "session.v1.json"

# Event types that may carry no content at all (INV-2: we record that typing happened, never what).
CONTENT_FREE_EVENTS = {
    "typing_burst",
    "shortcut",
    "enter",
    "marker",
    "capture_state",
    "focus",
    "click",
    "narration",
}


def png_size(path: Path) -> tuple[int, int]:
    """Width and height straight from the IHDR chunk, so the test needs no image library."""
    header = path.read_bytes()[:24]
    assert header[:8] == b"\x89PNG\r\n\x1a\n", f"{path} is not a PNG"
    return struct.unpack(">II", header[16:24])


def bundles() -> list[Path]:
    return sorted(p for p in HANDCRAFTED.iterdir() if p.is_dir())


def load(bundle: Path) -> dict:
    return json.loads((bundle / "session.json").read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def validator() -> Draft202012Validator:
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def test_there_are_five_bundles():
    assert len(bundles()) == 5


@pytest.mark.parametrize("bundle", bundles(), ids=lambda p: p.name)
def test_bundle_validates_against_the_session_schema(bundle: Path, validator: Draft202012Validator):
    errors = sorted(validator.iter_errors(load(bundle)), key=lambda e: list(e.path))
    assert not errors, "\n".join(f"{list(e.path)}: {e.message}" for e in errors[:5])


@pytest.mark.parametrize("bundle", bundles(), ids=lambda p: p.name)
def test_frames_exist_at_the_declared_size(bundle: Path):
    for frame in load(bundle)["frames"]:
        image = bundle / frame["image"]
        assert image.exists(), f"{bundle.name}: {frame['image']} is missing"
        assert png_size(image) == (frame["width"], frame["height"])


@pytest.mark.parametrize("bundle", bundles(), ids=lambda p: p.name)
def test_nothing_references_a_frame_or_segment_that_is_not_there(bundle: Path):
    session = load(bundle)
    frame_ids = {f["id"] for f in session["frames"]}
    segment_ids = {t["id"] for t in session["transcript"]}

    for event in session["events"]:
        if "frame_id" in event:
            assert event["frame_id"] in frame_ids, f"{bundle.name}: event points at {event['frame_id']}"
        if "segment_id" in event:
            assert event["segment_id"] in segment_ids

    for step in session.get("draft", {}).get("steps", []):
        assert set(step["frame_refs"]) <= frame_ids, f"{bundle.name}: step cites a missing frame"
        assert set(step.get("transcript_refs", [])) <= segment_ids


@pytest.mark.parametrize("bundle", bundles(), ids=lambda p: p.name)
def test_timeline_is_ordered_and_frames_are_redacted(bundle: Path):
    session = load(bundle)
    for name in ("events", "frames", "transcript"):
        stamps = [item["ts_ms"] for item in session[name]]
        assert stamps == sorted(stamps), f"{bundle.name}: {name} is out of order"

    # INV-1: a stored bundle only ever contains redacted frames.
    for frame in session["frames"]:
        assert frame["redaction_pending"] is False
        assert frame.get("redacted_at"), f"{bundle.name}: {frame['id']} has no redacted_at"


@pytest.mark.parametrize("bundle", bundles(), ids=lambda p: p.name)
def test_no_event_carries_keystroke_content(bundle: Path):
    # INV-2. typing_burst may count characters; nothing may hold them.
    for event in load(bundle)["events"]:
        assert event["type"] in CONTENT_FREE_EVENTS
        assert "text" not in event and "chars" not in event and "keys" not in event


def test_the_set_covers_the_chrome_and_screens_the_ui_work_needs():
    sessions = [load(b) for b in bundles()]
    tools = {s["remote_tool"]["kind"] for s in sessions}
    assert {"rdp", "screenconnect"} <= tools

    ocr = " ".join(f.get("ocr_text", "") for s in sessions for f in s["frames"])
    assert "Application Error" in ocr, "no error dialog in the set"
    assert "Password" in ocr, "no login prompt in the set"

    # One session must be a partial with no draft: Review has to render that state too.
    partials = [s for s in sessions if s["partial_capture"]]
    assert partials, "no partial-capture session"
    assert any("draft" not in s for s in sessions), "no session without a draft"
    assert any(s["frames_purged_unredacted"] > 0 for s in sessions)
    assert any(s["local_only"] for s in sessions)


def test_transcripts_are_already_scrubbed():
    for bundle in bundles():
        for segment in load(bundle)["transcript"]:
            text = segment["text"].lower()
            if "password is" in text or "password was" in text:
                assert "[REDACTED]" in segment["text"], (
                    f"{bundle.name}: unscrubbed password in {segment['id']}"
                )


def test_masked_frames_declare_their_regions():
    for bundle in bundles():
        for frame in load(bundle)["frames"]:
            if frame["sensitive_context"]:
                assert frame["masked_regions"], f"{bundle.name}: {frame['id']} is sensitive but masks nothing"
            for region in frame["masked_regions"]:
                assert region["width"] > 0 and region["height"] > 0
                assert 0 <= region["x"] < frame["width"]
                assert 0 <= region["y"] < frame["height"]


# ---- the redaction seed ---------------------------------------------------------------------------


def seed_labels() -> dict:
    return json.loads((SEED / "labels.json").read_text(encoding="utf-8"))


def test_seed_has_twenty_labelled_frames():
    labels = seed_labels()["frames"]
    assert len(labels) == 20
    assert len({entry["frame"] for entry in labels}) == 20


def test_every_seed_frame_exists_and_its_boxes_are_inside_it():
    for entry in seed_labels()["frames"]:
        image = SEED / entry["frame"]
        assert image.exists(), f"{entry['frame']} is missing"
        width, height = png_size(image)
        assert (width, height) == (entry["width"], entry["height"])
        for secret in entry["secrets"]:
            box = secret["box"]
            assert box["width"] > 0 and box["height"] > 0
            assert 0 <= box["x"] and box["x"] + box["width"] <= width
            assert 0 <= box["y"] and box["y"] + box["height"] <= height


def test_seed_covers_every_pattern_kind_and_carries_decoys():
    entries = seed_labels()["frames"]
    kinds = {secret["kind"] for entry in entries for secret in entry["secrets"]}
    assert {"card", "ssn", "password", "api_key", "email", "custom_pattern"} <= kinds

    # Decoys are the false-positive half of the gate: numbers that look sensitive and are not.
    decoys = [decoy for entry in entries for decoy in entry["decoys"]]
    assert len(decoys) >= 20
    assert any("Order" in decoy for decoy in decoys)
