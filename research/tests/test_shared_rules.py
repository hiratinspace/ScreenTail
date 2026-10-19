"""The rules both validators use, and the client library they must be at least as broad as (ST-061).

`prompts/shared-rules.json` is the one list: checks.py reads it, and the backend's SharedRulesTests holds
DraftValidator's constants to it. This file adds the third party. The client masks credentials on the
device with `PatternLibrary.cs`, and the 2026-10-12 review found the server's key shapes and cue words
narrower than the client's: a GitHub fine-grained token, a Google key, a JWT and a passcode were all
masked on the device and accepted in a draft. When the client learns a shape, this fails until the
shared list learns it too.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "prompts"))

import checks  # noqa: E402

RULES = json.loads((ROOT / "prompts" / "shared-rules.json").read_text(encoding="utf-8"))
LIBRARY = ROOT.parent / "client" / "ScreenTail.Core" / "Privacy" / "PatternLibrary.cs"


def client_pattern(name: str) -> str:
    """The pattern text of one of the client's generated regexes, as written in the source."""
    source = LIBRARY.read_text(encoding="utf-8")
    end = source.index(f"private static partial Regex {name}();")
    start = source.rindex("[GeneratedRegex(", 0, end)
    attribute = source[start:end]
    if '"""' in attribute:
        return attribute.split('"""')[1]
    return re.search(r'@"((?:[^"]|"")*)"', attribute).group(1)


def alternatives(pattern: str) -> list[str]:
    """The top-level alternatives inside the pattern's outermost group."""
    inner = pattern.strip()
    inner = inner[inner.index("(?:") + 3 : inner.rindex(")")]
    parts, depth, current, escaped = [], 0, "", False
    for char in inner:
        if escaped:
            current += char
            escaped = False
            continue
        if char == "\\":
            escaped = True
        elif char in "([":
            depth += 1
        elif char in ")]":
            depth -= 1
        elif char == "|" and depth == 0:
            parts.append(current)
            current = ""
            continue
        current += char
    parts.append(current)
    return [part.strip() for part in parts if part.strip()]


def verbose(pattern: str) -> str:
    """The client writes its key shapes with IgnorePatternWhitespace; this undoes it."""
    return re.sub(r"(?<!\\)\s+", "", pattern).replace("\\ ", " ")


def test_checks_uses_the_shared_file():
    assert checks.KEY_SHAPES == RULES["key_shapes"]
    assert checks.CONNECTIVES == set(RULES["connectives"])
    assert checks.REDACTION_MARKERS == set(RULES["redaction_markers"])


def test_every_key_shape_the_client_masks_is_one_the_validators_know():
    known = RULES["key_shapes"]
    client = alternatives(verbose(client_pattern("ApiKey")))
    assert len(client) >= 10, "The client's key shapes did not parse, so this test would check nothing."
    for shape in client:
        if shape.startswith("(?<="):
            # The client's cue-and-value rule. The validators answer it with the cue walk instead, so
            # what has to match is the list of words that introduce a key.
            group = re.match(r"\(\?<=\(\?i:(\(\?:[^)]*\))", shape).group(1)
            assert group in RULES["credential_cue"], (
                f"The client's key cues {group} are not all cue words here."
            )
            continue
        assert any(shape == ours or shape.startswith(ours) for ours in known), (
            f"The client masks {shape!r} and shared-rules.json has no key shape for it."
        )


def test_every_password_cue_the_client_knows_is_one_the_validators_know():
    ours = alternatives(RULES["credential_cue"]) + alternatives(RULES["pin_cue"])
    client = alternatives(client_pattern("PasswordCue"))
    assert len(client) >= 5, "The client's password cues did not parse, so this test would check nothing."
    for cue in client:
        assert cue in ours, f"The client treats {cue!r} as a password cue and the validators do not."


def test_the_ssn_shape_is_the_one_the_client_masks():
    assert client_pattern("Ssn").startswith(RULES["ssn"]), "The client's SSN shape and the shared one differ."
