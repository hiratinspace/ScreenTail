"""Post-conditions every draft must satisfy before a technician sees it (ST-061).

The prompt asks a model to behave; this decides whether it did. ST-063 runs these checks on each
response and treats a failure as a drafting failure — Review shows "couldn't draft" rather than a note
with an invented step in it. Keeping the rules here, in one list, is what makes the prompt's promises
testable instead of hopeful.

    errors = check_draft(draft, session)
    if errors:
        ...  # draft_failed, with the first error as the reason

Every rule states what went wrong in the words a person would use, because these strings end up in
diagnostics and, trimmed, in the Review banner.
"""

from __future__ import annotations

import re
from pathlib import Path

PROMPT = Path(__file__).resolve().parent / "note_v1.md"

# Quoted phrases in a step must be real quotations from the transcript.
QUOTE = re.compile(r"[\"“]([^\"”]{4,})[\"”]")
SSN = re.compile(r"\b(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}\b")
CARD_CANDIDATE = re.compile(r"\b(?:\d{13,19}|\d{3,6}(?:[ -]\d{3,6}){2,4})\b")


def prompt_version() -> str:
    """The version the prompt file declares — the single source, so a rename can't drift."""
    for line in PROMPT.read_text(encoding="utf-8").splitlines():
        if line.startswith("**Version:**"):
            return line.split("`")[1]
    raise AssertionError("note_v1.md does not declare a version")


def passes_luhn(candidate: str) -> bool:
    digits = [int(c) for c in candidate if c.isdigit()]
    if not 13 <= len(digits) <= 19:
        return False
    total = 0
    for index, digit in enumerate(reversed(digits)):
        if index % 2:
            digit *= 2
            if digit > 9:
                digit -= 9
        total += digit
    return total % 10 == 0


def normalise(text: str) -> str:
    return re.sub(r"\s+", " ", text).strip().lower()


def check_draft(draft: dict, session: dict) -> list[str]:
    """Returns the reasons this draft may not be shown. Empty means it's fine to store."""
    errors: list[str] = []
    frame_ids = {f["id"] for f in session.get("frames", [])}
    usable_frames = {f["id"] for f in session.get("frames", []) if not f.get("excluded_by_user")}
    segments = {t["id"]: t["text"] for t in session.get("transcript", [])}
    spoken = normalise(" ".join(segments.values()))

    if draft.get("prompt_version") != prompt_version():
        errors.append(f"Draft came from prompt {draft.get('prompt_version')!r}, not {prompt_version()!r}.")

    for index, step in enumerate(draft.get("steps", []), start=1):
        where = f"Step {index}"

        for frame_id in step.get("frame_refs", []):
            if frame_id not in frame_ids:
                errors.append(f"{where} cites frame {frame_id!r}, which is not in this session.")
            elif frame_id not in usable_frames:
                errors.append(f"{where} cites frame {frame_id!r}, which the technician excluded.")

        for segment_id in step.get("transcript_refs", []):
            if segment_id not in segments:
                errors.append(f"{where} cites transcript segment {segment_id!r}, which does not exist.")

        # Rule 3: nobody said it, so it can only have been read off the screen.
        if not step.get("transcript_refs") and step.get("confidence") != "low":
            errors.append(f"{where} has no transcript evidence, so it cannot be high confidence.")

        # Rule 4: a quotation has to be a quotation.
        for quoted in QUOTE.findall(step.get("text", "")):
            if normalise(quoted) not in spoken:
                errors.append(f"{where} quotes {quoted!r}, which nobody said in this session.")

        errors.extend(f"{where}: {reason}" for reason in check_no_secrets(step.get("text", "")))

    for field in ("problem", "result", "suggested_title", "kb_reason"):
        errors.extend(f"{field}: {reason}" for reason in check_no_secrets(str(draft.get(field, ""))))

    for follow_up in draft.get("follow_ups", []):
        errors.extend(f"follow_ups: {reason}" for reason in check_no_secrets(follow_up))

    if not str(draft.get("kb_reason", "")).strip():
        errors.append("kb_reason is empty; a person has to read it to decide about the article.")

    # Rule 9: the client rounds, so an unrounded suggestion longer than the session is a made-up number.
    minutes = draft.get("suggested_time_minutes")
    duration_ms = session.get("duration_ms")
    if isinstance(minutes, int) and isinstance(duration_ms, int):
        session_minutes = duration_ms / 60_000
        if minutes > session_minutes + 1:
            errors.append(
                f"Suggested time is {minutes} minutes for a session of "
                f"{session_minutes:.0f}; it should be the active time, unrounded."
            )

    return errors


def check_no_secrets(text: str) -> list[str]:
    """Rule 5's backstop. Redaction happens upstream; a model must never put a secret back."""
    reasons = []
    if SSN.search(text):
        reasons.append("contains something shaped like an SSN.")
    if any(passes_luhn(match) for match in CARD_CANDIDATE.findall(text)):
        reasons.append("contains something shaped like a card number.")
    return reasons
