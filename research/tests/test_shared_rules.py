"""The rules both validators use, and the client library whose key shapes they must cover (ST-061).

`prompts/shared-rules.json` is the one list: checks.py reads it, and the backend's SharedRulesTests holds
DraftValidator's constants to it. This file holds checks.py to it, and adds a third party. The client masks
credentials on the device with `PatternLibrary.cs`, and the 2026-10-12 review found the server's key shapes
narrower than the client's: a GitHub fine-grained token, a Google key and a JWT were masked on the device
and accepted in a draft. When the client learns a key shape, this fails until the shared list learns it.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "prompts"))

import checks  # noqa: E402

RULES = json.loads((ROOT / "prompts" / "shared-rules.json").read_text(encoding="utf-8"))
LIBRARY = ROOT.parent / "client" / "ScreenTail.Core" / "Privacy" / "PatternLibrary.cs"

NOT_PATTERNS = {
    "redaction_markers",
    "connectives",
    "ignore_case",
    "walk_steps",
    "sentence_ends",
    "value_breaks",
}
NOT_PATTERNS |= {"apostrophe_as"}

# A shorthand .NET's Regex and Python's re read differently, or that only one has. \s takes U+001C to
# U+001F here and not in .NET; \w and \b disagree about combining marks, connector punctuation and
# other-number characters; \d about digits outside the BMP; \p does not exist here. An escaped backslash
# before the letter is not one.
SHORTHAND = re.compile(r"(?<!\\)(?:\\\\)*\\[wWdDsSbBpPzZAG]")


def patterns():
    for name, value in RULES.items():
        if name.startswith("_") or name in NOT_PATTERNS:
            continue
        values = value.values() if isinstance(value, dict) else value if isinstance(value, list) else [value]
        for pattern in values:
            yield name, pattern


@pytest.mark.parametrize("name,pattern", list(patterns()), ids=lambda v: str(v)[:40])
def test_no_rule_uses_a_shorthand_the_engines_read_differently(name: str, pattern: str):
    assert not SHORTHAND.search(pattern), f"{name} uses {SHORTHAND.search(pattern).group()!r}"
    assert not SHORTHAND.search(checks.expand(pattern)), f"{name}, written out, uses a shorthand"


def test_checks_compiles_every_rule_from_the_shared_file():
    compiled = {
        "quoted": checks.QUOTED,
        "apostrophe": checks.APOSTROPHE,
        "ssn": checks.SSN,
        "ssn_cued": checks.SSN_CUED,
        "card": checks.CARD,
        "credential_cue": checks.CREDENTIAL_CUE,
        "secret_shaped": checks.SECRET_SHAPED,
        "key_shapes": checks.KEY_SHAPE,
        "key_cue_value": checks.KEY_CUE_VALUE,
        "directive": checks.DIRECTIVE,
    }
    rules = {name for name in RULES if not name.startswith("_")}
    assert rules == set(compiled) | NOT_PATTERNS | {"placeholders"}, (
        "checks.py does not use every shared rule"
    )
    for name, regex in compiled.items():
        assert regex.pattern == checks.expand(
            "|".join(RULES[name]) if isinstance(RULES[name], list) else RULES[name]
        )
        ignores_case = bool(regex.flags & re.IGNORECASE)
        assert ignores_case == (name in RULES["ignore_case"]), f"{name} ignores case in one validator only"
        assert regex.flags & re.ASCII, f"{name} folds case beyond ASCII, which .NET does not"
    assert checks.CONNECTIVES == set(RULES["connectives"])
    assert checks.WALK_STEPS == int(RULES["walk_steps"])


def client_key_shapes() -> list[str]:
    """The client's key shapes, one alternative per line of its ApiKey pattern, with its escaped spaces
    read as spaces. Its pattern is written with IgnorePatternWhitespace."""
    source = LIBRARY.read_text(encoding="utf-8")
    end = source.index("private static partial Regex ApiKey();")
    body = source[source.rindex("[GeneratedRegex(", 0, end) : end].split('"""')[1]
    lines = [line.strip() for line in body.splitlines()]
    shapes = [line.removeprefix("| ").strip() for line in lines if line and line not in ("(?:", ")")]
    return [shape.replace("\\ ", " ") for shape in shapes]


def test_every_key_shape_the_client_masks_is_one_the_validators_refuse():
    shared = set(RULES["key_shapes"])
    missing = []
    for shape in client_key_shapes():
        if shape.startswith("(?<="):
            continue  # The cue-and-value rule, compared below.
        if shape.startswith("-----BEGIN"):
            # The client masks the whole block; refusing the header is enough to refuse the draft.
            shape = shape[: shape.index("(?s:")]
        if shape not in shared:
            missing.append(shape)
    assert not missing, f"the client masks these and the validators do not know them: {missing}"
    assert len(client_key_shapes()) >= 13, "the client's key shapes were not read"


def test_the_named_keys_are_the_clients():
    # The client's cue-and-value rule names the kinds of key whose long value it masks. A kind it adds is
    # a kind the server has to know.
    cue = next(shape for shape in client_key_shapes() if shape.startswith("(?<="))
    client = re.search(r"\(\?:((?:[a-z]+\|)+[a-z]+)\)\[ _-\]\?", cue).group(1).split("|")
    ours = re.match(r"\(\?:((?:[a-z]+\|)+[a-z]+)\)", RULES["key_cue_value"]).group(1).split("|")
    assert ours == client
