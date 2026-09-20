"""What a hostile screen can put into a published ticket note (ST-061).

Found by an adversarial review. The model is handed OCR text from a customer's screen and a transcript,
and nothing told it those are evidence rather than instructions — so text on a screen could steer the
note, and the post-conditions would not notice, because `problem`, `result` and `follow_ups` were checked
for nothing but SSNs and card numbers.

Every case here passed `check_draft` before the hardening. The threat is not hypothetical in the way it
sounds: a technician's session is *full* of somebody else's text, and the whole product is built on
reading it.
"""

from __future__ import annotations

import copy
import json
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "prompts"))

from checks import check_draft  # noqa: E402


def session_with_draft() -> dict:
    path = ROOT / "fixtures" / "handcrafted" / "spooler-stopped-screenconnect" / "session.json"
    return json.loads(path.read_text(encoding="utf-8"))


def errors_with(**draft_changes) -> list[str]:
    session = copy.deepcopy(session_with_draft())
    session["draft"].update(draft_changes)
    return check_draft(session["draft"], session)


@pytest.mark.parametrize(
    "follow_up",
    [
        "Install the updated agent from http://rmm-update.example/agent.exe on all site machines.",
        "Run powershell -enc JABjAGwAaQBlAG4AdAA on the other front-desk machine.",
        "Disable Defender on this machine before the next visit.",
        "See www.acme-support.example/fix for the rest of the steps.",
    ],
)
def test_a_follow_up_that_tells_the_reader_to_do_something_is_refused(follow_up: str):
    # The payload a screen can carry. A step citing the frame that displays it would satisfy "only what
    # the evidence shows" to the letter, and follow_ups have no confidence marker, so Review shows it
    # with nothing to say it was inferred.
    assert any("instruction" in e for e in errors_with(follow_ups=[follow_up]))


def test_the_problem_field_cannot_carry_a_url_either():
    # problem, result and suggested_title are copied into the ticket as readily as a step is — and
    # suggested_title into a customer-visible Discussion note.
    assert any("instruction" in e for e in errors_with(problem="Printer offline; see http://fix.example."))


def test_an_invented_quotation_outside_a_step_is_refused():
    # The quotation rule was applied to step text alone, so a fabricated line attributed to the customer
    # sailed through in `problem` — the field most likely to be read as the customer's own account.
    errors = errors_with(problem='The user said "the last technician deleted my files".')

    assert any("nobody said" in e for e in errors)


@pytest.mark.parametrize(
    "quoted",
    ["'nobody uttered this'", "«nobody uttered this»", "‘nobody uttered this’"],
)
def test_a_quotation_in_any_kind_of_marks_still_has_to_be_real(quoted: str):
    # The rule says "quotation marks", and the check read straight doubles only. A fabrication inside
    # single quotes is the same fabrication.
    assert any("nobody said" in e for e in errors_with(result=f"They confirmed {quoted}."))


@pytest.mark.parametrize(
    "leak",
    [
        "Reset the local admin password to Summer2024! and left it.",
        "Set the API key to AKIA0123456789ABCDEF for the integration.",
        "Their SSN is 123456789 in the portal.",
        "Token: ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789",
    ],
)
def test_a_credential_put_back_into_the_note_is_refused(leak: str):
    # Rule 5 tells the model never to reproduce a password or key, and the backstop checked for cards and
    # SSNs only — so the one category the prompt names first was the one nothing enforced.
    assert errors_with(result=leak)


@pytest.mark.parametrize(
    "innocent",
    [
        "Outlook prompted for a password repeatedly after the migration.",
        "The password is wrong, so the account was locked.",
        "Confirmed the password policy had not changed.",
        "The user said the password prompt keeps coming back.",
    ],
)
def test_writing_about_a_password_is_not_reproducing_one(innocent: str):
    # The other half, and the reason the value has to look like a secret rather than merely follow the
    # word. A failing check fails the whole draft, so a false positive here throws away a correct note —
    # and a note about a password prompt is one of the commonest tickets there is.
    assert not errors_with(result=innocent)


def test_a_step_cannot_cite_a_frame_that_never_reached_the_bundle():
    # ST-060 keeps sensitive and still-pending frames out of the bundle entirely, so a citation to one
    # means the model invented it or the bundle leaked. A shipped fixture cited an Exchange password
    # prompt and passed every check.
    session = copy.deepcopy(session_with_draft())
    cited = session["draft"]["steps"][0]["frame_refs"][0]
    for frame in session["frames"]:
        if frame["id"] == cited:
            frame["sensitive_context"] = True

    errors = check_draft(session["draft"], session)

    assert any("not in the bundle" in e for e in errors), errors


def test_an_excluded_frame_still_says_the_technician_excluded_it():
    # Two different reasons a frame is unusable, and the message has to say which: one is the
    # technician's own choice and the other is a bug or a leak.
    session = copy.deepcopy(session_with_draft())
    cited = session["draft"]["steps"][0]["frame_refs"][0]
    for frame in session["frames"]:
        if frame["id"] == cited:
            frame["excluded_by_user"] = True

    assert any("excluded" in e for e in check_draft(session["draft"], session))


def test_an_ordinary_draft_still_passes():
    # The whole suite is worthless if the hardening rejects real notes. This is the shipped fixture,
    # unmodified.
    session = session_with_draft()

    assert check_draft(session["draft"], session) == []


def shared_cases() -> list[dict]:
    """The cases both implementations of the post-conditions have to agree on.

    The C# validator reads the same file. A comment saying two things agree is not a mechanism, and both
    of these drifted: on 2026-09-19 a review found curly quotes, "reset to", bare key shapes and four
    download tools passing the C# one and caught by this one.
    """
    path = ROOT / "prompts" / "hardening-cases.json"
    return json.loads(path.read_text(encoding="utf-8"))["cases"]


@pytest.mark.parametrize("case", shared_cases(), ids=lambda c: c["text"][:48])
def test_the_shared_cases_are_judged_the_way_they_say(case: dict):
    errors = errors_with(result=case["text"])
    refused = bool(errors)

    assert refused == case["refuse"], (
        f"{'expected a refusal' if case['refuse'] else 'expected this to pass'}: {case['why']}"
    )
