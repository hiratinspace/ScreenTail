# ruff: noqa: E501 -- MAIN_DIRECTIVE below is copied verbatim from C#, long lines and all.
"""The cases both validators were found to agree on, and production's instruction rule (ST-061).

`prompts/differential.py` wrote `prompts/differential-cases.json` after asking Python's re and .NET's
Regex about every case and finding them agree on every field. The backend's DifferentialCasesTests runs
the same file and checks each pinned "main" verdict against production's rule itself; this suite holds
checks.py to the file, and holds the pinned verdicts to the owner's condition: the instruction verdict is
production's, except where the new normalisation turns a pass into a refusal, never the other way.
"""

from __future__ import annotations

import copy
import json
import re
import sys
import unicodedata
from collections import Counter
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
REPO = ROOT.parent
sys.path.insert(0, str(ROOT / "prompts"))

import checks  # noqa: E402

CORPUS = json.loads((ROOT / "prompts" / "differential-cases.json").read_text(encoding="utf-8"))["cases"]
HARDENING = json.loads((ROOT / "prompts" / "hardening-cases.json").read_text(encoding="utf-8"))["cases"]
FIXTURE = ROOT / "fixtures" / "handcrafted" / "spooler-stopped-screenconnect" / "session.json"
GROUPS = sorted({case["group"] for case in CORPUS})

# Production's instruction rule before ST-061, verbatim from DraftValidator.cs at main b989436. It is
# .NET's, so it is not run here: engine_probe.cs ran it to pin "main" and "main_on_read", and the backend
# suite runs it again on every case. This copy is what the other two are checked against below.
MAIN_DIRECTIVE = r"""
        @"(?:https?://|www\.)\S+"
        + @"|\b(?:Set-ExecutionPolicy|powershell|cmd\.exe|regedit|curl|wget|iwr|irm|invoke-webrequest"
        + @"|invoke-restmethod|invoke-expression|iex|certutil|bitsadmin|mshta|rundll32)\b"
        + @"|\b(?:disable|turn\s+off|uninstall|remove)\s+(?:the\s+)?(?:antivirus|defender|firewall|edr|mfa|two-factor)\b"
        + @"|\b(?:download|install|run|execute|disable|uninstall|delete)\b\s+(?:the\s+|your\s+|windows\s+)?\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
"""


def text_of(value) -> str:
    return value if isinstance(value, str) else "".join(value)


def case_text(case: dict) -> str:
    return text_of(case["text"] if "text" in case else case["parts"])


SESSION = json.loads(FIXTURE.read_text(encoding="utf-8"))


def refused(text: str) -> bool:
    draft = copy.deepcopy(SESSION["draft"])
    draft["result"] = text
    return bool(checks.check_draft(draft, SESSION))


@pytest.mark.parametrize("group", GROUPS)
def test_every_case_reads_the_way_both_validators_agreed(group: str):
    # Character for character: the two normalisations are the same function, or the rules after them are
    # handed different text.
    wrong = [
        case_text(c)
        for c in CORPUS
        if c["group"] == group and checks.read(case_text(c)) != text_of(c["reads_as"])
    ]
    assert not wrong, f"{len(wrong)} cases read differently, such as {wrong[:3]!r}"


@pytest.mark.parametrize("group", GROUPS)
def test_every_case_is_judged_the_way_both_validators_agreed(group: str):
    wrong = []
    for case in (c for c in CORPUS if c["group"] == group):
        text = case_text(case)
        instruction = checks.instruction(text) is not None
        if instruction != case["instruction"] or refused(text) != case["refuse"]:
            wrong.append(text)
    assert not wrong, f"{len(wrong)} cases are judged differently, such as {wrong[:3]!r}"


def test_the_instruction_verdict_is_productions_except_where_normalisation_adds_a_refusal():
    outcome = Counter()
    for case in CORPUS:
        if case["instruction"] == case["main"]:
            outcome["same"] += 1
        elif case["main"]:
            outcome["lost"] += 1
        elif case["main_on_read"]:
            outcome["added by normalisation"] += 1
        else:
            outcome["unexplained"] += 1
    assert outcome["lost"] == 0, "a sentence production refused as an instruction now passes"
    assert outcome["unexplained"] == 0, "a sentence is newly refused, and not because of the normalisation"
    assert outcome["added by normalisation"] > 0, "nothing exercises the normalisation"
    assert outcome["same"] > 9 * outcome["added by normalisation"]


def test_the_corpus_holds_every_hardening_case():
    corpus = {case_text(case) for case in CORPUS}
    missing = [case_text(case) for case in HARDENING if case_text(case) not in corpus]
    assert not missing, f"run prompts/differential.py again; it does not have {missing!r}"


def oracle_in(path: Path) -> str:
    """The verbatim copy of production's rule in a .NET source file: the lines from its first pattern
    line to its options."""
    source = path.read_text(encoding="utf-8")
    start = source.index('        @"(?:https?://|www\\.)\\S+"')
    end = source.index("RegexOptions.Compiled | RegexOptions.IgnoreCase,", start)
    return source[start : end + len("RegexOptions.Compiled | RegexOptions.IgnoreCase,")]


@pytest.mark.parametrize(
    "path",
    [
        ROOT / "prompts" / "engine_probe.cs",
        REPO / "backend" / "tests" / "ScreenTail.Api.Tests" / "Summarize" / "DifferentialCasesTests.cs",
    ],
    ids=lambda p: p.name,
)
def test_every_copy_of_productions_rule_is_the_same_verbatim_copy(path: Path):
    assert oracle_in(path) == MAIN_DIRECTIVE.strip("\n").removesuffix("\n"), f"{path.name} has another rule"


def test_the_shared_directive_is_productions_with_its_shorthand_written_out():
    # The rule both validators use, read back into production's spelling, is production's rule.
    shared = "|".join(checks.RULES["directive"])
    production = "".join(re.findall(r'@"([^"]*)"', MAIN_DIRECTIVE))
    spelled = (
        shared.replace("%start%", r"\b")
        .replace("%end%", r"\b")
        .replace("%nonspace%", r"\S")
        .replace("%space%", r"\s")
    )
    assert spelled == production


def test_the_shared_tables_give_the_same_nfkc_as_unicodedata():
    # Over the code points both the tables' Unicode version and this Python's know. A decomposition never
    # changes once a code point is assigned, so this holds whichever Python runs it.
    assigned = set(checks.code_points(checks.TABLES["assigned"]))
    wrong = []
    for cp in assigned:
        char = chr(cp)
        if 0xD800 <= cp <= 0xDFFF or unicodedata.category(char) == "Cn":
            continue
        if "".join(map(chr, checks.nfkc([cp]))) != unicodedata.normalize("NFKC", char):
            wrong.append(f"U+{cp:04X}")
    assert not wrong, f"{len(wrong)} code points normalise differently: {wrong[:10]}"
