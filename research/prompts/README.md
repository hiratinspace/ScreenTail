# Prompts

Versioned prompt files and their output contract. `note_v1.md` (ST-061) is the session-to-note prompt:
strict output schema, post-conditions that reject invented steps, fake quotations, dangling frame
references and leaked secrets. Any prompt change runs the eval harness in CI (Guide §5); the harness's
draft-quality half arrives with ST-062, on ST-030's golden sessions. `note_local_v1.md` arrives with ST-065.

## The post-conditions, in two languages

The post-conditions run twice: `checks.py` scores the eval corpus offline, and the backend's
`DraftValidator` refuses drafts in front of a technician. They are one rule set (ST-061):

- `shared-rules.json` holds the patterns and word lists. `checks.py` reads it; the backend keeps the same
  values in `DraftRules.cs`, and `SharedRulesTests` fails when the two differ.
- `unicode-tables.json` holds the character data both read text with: NFKC tables, and the characters
  .NET's Regex reads as `\b`'s word characters, `\d` and `\s`, which the shared patterns spell out instead
  of using shorthands the two engines read differently. `unicode_tables.py` writes it, and
  `unicode_tables.py --check` compares the two validators on every code point.
- `hardening-cases.json` says what both must refuse and let through.
- `differential-cases.json` holds cases both were asked about and agreed on, with production's
  instruction verdict from before ST-061 beside each. `differential.py` writes it, and stops if the two
  disagree or if any case production refused as an instruction would now pass.

- `agreement-cases.json.gz` holds what both must read every scalar value as, and every pair and triple
  the tables compose or reorder, Hangul in each shape, long runs of marks, 112,654 seeded random
  sequences, and text either side of the reading's bound, as digests of Python's own NFKC at the tables'
  Unicode version. `agreement.py` writes it; `AgreementCasesTests` and `test_agreement_cases.py` hold
  each validator to it, and `agreement.py --check` names any case one of them reads differently.

A text is read only while its decomposition stays within `read_growth` (4) code points for each of its
own and `read_slack` (64) more. Past that both refuse it unread, because NFKC makes U+FDFA eighteen and
the backend reads a device's transcript.

`unicode_tables.py`, `differential.py` and `agreement.py --check` need the .NET SDK; they ask .NET through
`engine_probe.cs`.
