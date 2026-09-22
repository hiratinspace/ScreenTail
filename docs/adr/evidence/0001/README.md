# ADR-0001 evidence: the ST-001 spike runs

Raw reports from the runs that decided [ADR-0001](../../0001-client-stack.md). Each folder is one run;
the report's first lines say which machine, what load and how long. Nothing here is edited after the run.

| Folder | Machine | When | What it is |
|---|---|---|---|
| `hosted-runner/` | GitHub `windows-latest`, 2 cores, no microphone | 2026-09-11 | `smoke-run/` one minute with everything on; `legibility/` the 4K→1600 px table with its JPEGs; `overlay/` the `WDA_EXCLUDEFROMCAPTURE` check. **Its numbers do not count** — the ADR says why — but its pass/fail verdicts do |
| `baseline/` | The laptop, a person typing | 2026-09-11 23:16 | Five minutes, hooks only (Whisper, UIA and OCR off). The input-latency floor |
| `full-load/` | The laptop, a person typing | 2026-09-11 23:22 | Five minutes with everything on. **The AC1 and AC3 pass the ADR was accepted on** |
| `laptop-synthetic/` | The laptop, `SendInput` typing into a target window | 2026-09-12 00:12 | Five minutes, everything on, 2,368 synthetic keys. The unattended shape the hardware workflow still uses. AC4 was not exercised in this run |
| `legibility/` | The laptop | 2026-09-11 | The laptop's 4K→1600 px legibility table and JPEGs (finding 2a) |
| `overlay/` | The laptop | 2026-09-11 | The laptop's overlay check; the ADR's evidence table links these two PNGs |

The ADR's Evidence section is the reading order; this file only says what each folder is.
