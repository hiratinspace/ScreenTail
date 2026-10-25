"""Writes differential-cases.json: the cases both draft validators must read and judge the same way (ST-061).

checks.py runs on Python's re and DraftValidator.cs on .NET's Regex, and the two engines read the same
pattern differently: \\s, \\b, \\w, \\d and IGNORECASE all disagree somewhere. The shared rules no longer
use any of them (shared-rules.json, unicode-tables.json). This file is how that stays true, and how the
instruction rule is shown to be production's.

It takes every shared hardening case, sentences built on each command, tool, security control and web
address production's instruction rule names, and credential sentences, and puts a troublesome character
at the places a rule looks: before a word, inside it, after it, and in place of the space after it. The
characters are separators .NET and Python disagree about, format characters nobody sees, fullwidth and
other compatibility forms, combining marks, letters and digits outside ASCII and outside the BMP, and the
case-folding oddities. It adds sequences of combining marks, which exercise NFKC's ordering and
composition.

For each case it pins, after asking both validators:
- "reads_as": the text as read, which checks.read and DraftValidator.Read must give character for
  character. For a case kept in parts, the parts, which it reads as unchanged.
- "instruction": whether the instruction rule refuses it.
- "refuse": whether a draft whose result is the text is refused.
- "main" and "main_on_read": production's instruction rule from before ST-061, copied verbatim from
  DraftValidator.cs at main b989436 and run by .NET with production's InvariantGlobalization, on the text
  and on the text as read.

Nothing is written unless Python and .NET give the same answer for every one of these on every case, and
unless every case keeps production's instruction verdict or turns from pass to refuse with main_on_read
true (production's rule refuses the text once normalised). A case that turns from refuse to pass stops
the run. Run it again after changing the shared rules or the hardening cases:

    .venv/bin/python prompts/differential.py

It needs the .NET SDK, and asks .NET through engine_probe.cs.
"""

from __future__ import annotations

import copy
import json
import random
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "differential-cases.json"
sys.path.insert(0, str(HERE))

import checks  # noqa: E402
from unicode_tables import probe  # noqa: E402

FIXTURE = HERE.parent / "fixtures" / "handcrafted" / "spooler-stopped-screenconnect" / "session.json"

# Each sentence names its keyword: the word or address production's instruction rule looks for.
INSTRUCTIONS = [
    ("Install the agent from the share.", "Install"),
    ("Then download the installer before the visit.", "download"),
    ("Then run setup.exe on every machine.", "run"),
    ("Then execute the script as admin.", "execute"),
    ("Then delete your profile and log in again.", "delete"),
    ("Then uninstall windows updates from last week.", "uninstall"),
    ("Then disable the proxy for the session.", "disable"),
    ("Then powershell -c the fix.", "powershell"),
    ("Then cmd.exe /c the fix.", "cmd.exe"),
    ("Then curl -s the fix.", "curl"),
    ("Then iex the fix.", "iex"),
    ("Then certutil -urlcache the fix.", "certutil"),
    ("Then invoke-webrequest the fix.", "invoke-webrequest"),
    ("Then Set-ExecutionPolicy Bypass.", "Set-ExecutionPolicy"),
    ("Then disable Defender before the visit.", "Defender"),
    ("Then turn off the firewall before the visit.", "firewall"),
    ("Then remove the antivirus before the visit.", "antivirus"),
    ("See https://example.invalid/fix for the steps.", "https://"),
    ("See www.example.invalid for the steps.", "www."),
    # Records production refuses as well. A difference here would be a change to which records pass.
    ("The scheduled backup did not run overnight.", "run"),
    ("The install failed at 40 percent.", "install"),
]

CREDENTIALS = [
    ("The new password is Winter2026!", "password", "Winter2026!"),
    ("password: Tr0ub4dor&3", "password", "Tr0ub4dor&3"),
    ("The password is, uh, Winter2026", "password", "Winter2026"),
    ("The api key is Xy7pQ2rS9t for the vault.", "api key", "Xy7pQ2rS9t"),
    ("The passcode is Gate4821x for the side door.", "passcode", "Gate4821x"),
    ("SSN 123456789 was showing in the record.", "SSN", "123456789"),
    ("The card 4111 1111 1111 1111 was on screen.", "card", "4111"),
    ("Password reset completed for jsmith.", "Password", "jsmith"),
    ("The password was changed by the user at 10:40.", "password", "10"),
    ("The PIN pad at reception was replaced.", "PIN", "pad"),
]

TROUBLE = [
    # Separators: white space to one engine and not the other, or to neither, or a line break.
    "\t",
    "\n",
    "\r",
    "\x0b",
    "\x0c",
    "\x1c",
    "\x1d",
    "\x1e",
    "\x1f",
    "\x85",
    "\xa0",
    "\u1680",
    "\u2000",
    "\u2007",
    "\u200a",
    "\u2028",
    "\u2029",
    "\u202f",
    "\u205f",
    "\u3000",
    "  ",
    # Format characters nobody sees, including a bidirectional override and an astral tag.
    "\u200b",
    "\u200c",
    "\u200d",
    "\u2060",
    "\ufeff",
    "\u00ad",
    "\u180e",
    "\u200e",
    "\u202e",
    "\u2066",
    "\U000e0020",
    # Combining marks and a variation selector.
    "\u0301",
    "\u0308",
    "\ufe0f",
    # Word characters to one engine and not the other, outside ASCII, outside the BMP.
    "\u00e9",
    "\u02bc",
    "_",
    "\u203f",
    "\u00b2",
    "\u2460",
    "\u216b",
    "\u0663",
    "\U0001d400",
    "\U0001d7cf",
    "\U00010400",
    "\U0001f600",
    # Case folding: what Python folds into ASCII and .NET does not, or the other way.
    "\u0130",
    "\u0131",
    "\u017f",
    "\u212a",
    "\u1e9e",
    # Compatibility forms: a mark that NFKC turns into a space and a mark, dots, colons, a fullwidth letter.
    "\u00b4",
    "\u00a8",
    "\u2024",
    "\ufe52",
    "\uff0e",
    "\uff1a",
    "\uff41",
]


def places(sentence: str, keyword: str) -> list[str]:
    """The sentence with each troublesome character before, inside and after the keyword, and in place
    of the space after it."""
    at = sentence.index(keyword)
    end = at + len(keyword)
    out = []
    for char in TROUBLE:
        out.append(sentence[:at] + char + sentence[at:])
        out.append(sentence[: at + 1] + char + sentence[at + 1 :])
        out.append(sentence[:end] + char + sentence[end:])
        if sentence[end : end + 1] == " ":
            out.append(sentence[:end] + char + sentence[end + 1 :])
    return out


def sequences(count: int) -> list[str]:
    """Bases and combining marks in random order, which NFKC has to reorder and compose."""
    marks = [chr(cp) for cp in range(0x300, 0x370)] + [
        "\u093c",
        "\u094d",
        "\u05b0",
        "\u0f71",
        "\u0f72",
        "\u3099",
    ]
    bases = list("aeiouAEIOUnNcCsS") + ["\u1100", "\u1161", "\u11a8", "\uac00", "\u0b47", "\u0b3e", "\u304b"]
    rng = random.Random(61)
    return [
        "".join(rng.choice(bases + marks) for _ in range(rng.randint(2, 6))) + " run it" for _ in range(count)
    ]


def hardening() -> list[dict]:
    cases = json.loads((HERE / "hardening-cases.json").read_text(encoding="utf-8"))["cases"]
    return [{"parts": case["parts"]} if "parts" in case else {"text": case["text"]} for case in cases]


def text_of(case: dict) -> str:
    return case["text"] if "text" in case else "".join(case["parts"])


def refused(text: str, session: dict) -> bool:
    session = copy.deepcopy(session)
    session["draft"]["result"] = text
    return bool(checks.check_draft(session["draft"], session))


def main() -> None:
    session = json.loads(FIXTURE.read_text(encoding="utf-8"))
    cases: list[dict] = [dict(group="hardening", **case) for case in hardening()]
    for sentence, keyword in INSTRUCTIONS:
        cases += [{"group": "instruction", "text": text} for text in places(sentence, keyword)]
    for sentence, cue, value in CREDENTIALS:
        variants = places(sentence, cue) + places(sentence, value)
        cases += [{"group": "credential", "text": text} for text in variants]
    cases += [{"group": "sequence", "text": text} for text in sequences(300)]

    seen, unique = set(), []
    for case in cases:
        if text_of(case) not in seen:
            seen.add(text_of(case))
            unique.append(case)
    cases = unique

    texts = [text_of(case) for case in cases]
    net = probe({"mode": "cases", "texts": texts})

    disagreements, refuse_to_pass, unexplained = [], [], []
    for case, text, answer in zip(cases, texts, net, strict=True):
        ours = {
            "reads_as": checks.read(text),
            "instruction": checks.instruction(text) is not None,
            "refuse": refused(text, session),
        }
        theirs = {name: answer[name if name != "reads_as" else "read"] for name in ours}
        if ours != theirs:
            disagreements.append((text, ours, theirs))
        main_verdict, on_read, new = answer["main"], answer["main_on_read"], ours["instruction"]
        if main_verdict and not new:
            refuse_to_pass.append(text)
        elif new and not main_verdict and not on_read:
            unexplained.append(text)
        case.update(ours, main=main_verdict, main_on_read=on_read)
        if "parts" in case:
            # Kept in parts, as the hardening case is, so push protection does not stop the file.
            if ours["reads_as"] != text:
                sys.exit(f"A case kept in parts reads differently from how it is written: {case['parts']}")
            case["reads_as"] = case["parts"]

    for text, ours, theirs in disagreements[:20]:
        print(f"Python and .NET disagree on {text!r}: {ours} against {theirs}")
    for text in refuse_to_pass[:20]:
        print(f"Production refused {text!r} as an instruction and this rule set lets it through.")
    for text in unexplained[:20]:
        print(f"{text!r} is newly refused, and not because production's rule refuses it once normalised.")
    if disagreements or refuse_to_pass or unexplained:
        sys.exit(
            f"Not written: {len(disagreements)} disagreements, {len(refuse_to_pass)} refusals lost, "
            f"{len(unexplained)} unexplained new refusals."
        )

    changed = sum(1 for case in cases if case["instruction"] != case["main"])
    document = {
        "_comment": [
            "Cases both draft validators must read and judge the same way. Written by differential.py; do",
            "not edit. Python and .NET agreed on every field of every case before it was written.",
            "'main' and 'main_on_read' are production's instruction rule from before ST-061",
            "(DraftValidator.cs at main b989436), run by .NET on the text and on the text as read.",
            "'instruction' equals 'main' except where normalisation turns a pass into a refusal, which is",
            "where 'main_on_read' is true.",
            f"{len(cases)} cases; {changed} change the instruction verdict, all from pass to refuse.",
        ],
        "cases": cases,
    }
    lines = ["{", f'  "_comment": {json.dumps(document["_comment"], ensure_ascii=True)},', '  "cases": [']
    lines += [f"    {json.dumps(case, ensure_ascii=True)}," for case in cases]
    lines[-1] = lines[-1].rstrip(",")
    lines += ["  ]", "}"]
    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(
        f"wrote {OUT.relative_to(HERE.parent)}: {len(cases)} cases, {changed} pass to refuse, 0 disagreements"
    )


if __name__ == "__main__":
    main()
