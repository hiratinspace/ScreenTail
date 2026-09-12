# Fixtures

Sessions to build against before the capture engine exists (ST-006). Review, the note prompt and the
redaction evaluation all point here, so these files are tools: if one drifts from the schema, something
downstream is quietly broken. `research/tests/test_fixtures.py` is what keeps them honest.

Everything here is **synthetic**. No real customer, technician, card, account or credential appears in
any of it. The frames are drawn by `tools/render_fixtures.py`, not captured.

## `handcrafted/` — five session bundles

Each directory holds a `session.json` (a full `session.v1` document) and its `frames/*.png`, referenced
by the relative path in `frame.image`. They load with no backend and no service running.

| Bundle | Tool | What it's for |
|---|---|---|
| `spooler-stopped-screenconnect` | ScreenConnect | The everyday case: stopped print spooler, restarted, confirmed. Three high-confidence steps, a KB candidate. |
| `outlook-profile-rdp` | RDP | A **login prompt**: capture suppresses itself on the password field, the frame stores the value masked, and the transcript says `[REDACTED]`. One low-confidence step. |
| `app-crash-dialog-screenconnect` | ScreenConnect | An **error dialog** carrying the diagnosis (`0x80070005`); the note has to quote the code. Crosses into an admin tool mid-session. |
| `vpn-billing-portal-rdp` | RDP | A card number on screen, already masked in the stored frame, with a `sensitive_context` suppression on the timeline. Local-only, locally drafted. |
| `interrupted-partial-capture` | ScreenConnect | What a lost session looks like: `partial_capture: true`, three frames purged before redaction, **no draft**. Review must render this state too. |

Between them they cover both chrome styles, every capture state, both step confidences, both draft
sources, a KB candidate and a rejection, and a session with no draft at all.

## `redaction-seed/` — 20 labelled frames

Input for the ST-042 evaluation. Each frame shows one secret **unmasked** — that's the point — beside a
decoy that looks sensitive and isn't (an order number, an extension, an invoice reference). `labels.json`
gives the ground truth: kind, text, and the exact box, since the renderer knows where it drew it.

Covers cards (spaced, hyphenated, bare, 15- and 16-digit), SSNs, passwords in fields and chat, AWS /
GitHub / Slack / OpenAI key shapes, emails and tenant asset tags. Recall is scored against `secrets`;
false positives against `decoys`.

## Regenerating

```bash
python -m pip install -r tools/requirements.txt
python tools/render_fixtures.py
pytest ../tests/test_fixtures.py
```

Edit the scenarios in the script, never the generated JSON: the boxes in `masked_regions` and
`labels.json` are computed from where the text actually landed.

Rendering is deterministic for a given font, so a PNG diff normally means the source changed — but the
script draws with Arial from `/System/Library/Fonts/Supplemental`, and on a machine without it Pillow
falls back to its own font and every glyph moves. That's why CI validates the committed files rather
than regenerating them: regenerate on macOS, or expect a whole-set diff.

## What these fixtures are *not*

They are clean, flat renders with one font. **Nothing about OCR accuracy, scaling or compression can be
judged on them**, and a redaction score measured here is a floor, not a result. The real corpus — staged
captures from a Windows VM with real applications, fonts and DPI — is **ST-030**, and the recall gate in
the ST-042 acceptance criteria has to run against that. Treat these as the unit-test tier.
