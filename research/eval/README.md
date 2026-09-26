# Evaluation harness

| Script | Measures | Ticket |
|---|---|---|
| `wer.py` | Word error rate between a reference narration and what the speech pipeline produced | ST-027 |
| `draft_quality.py` | Draft quality: edit ratio, hallucinated steps, rubric, and the regression gate | ST-062 |
| — | Redaction recall and false-positive rate against the labelled corpus | ST-042, needs ST-030 |

## `wer.py`

```
python research/eval/wer.py reference.txt transcript.txt
python research/eval/wer.py reference.txt transcript.txt --json --budget 0.15
```

ST-027 budgets **WER ≤ 15%** on a ten-minute narration with the default model. `--budget` exits 1 when the
number is above it, so CI can hold the line once there is audio to run it on.

**Normalisation is part of the definition.** The comparison folds case, closes up hyphens and apostrophes,
drops the remaining punctuation and collapses whitespace — so `Wi-Fi`/`wifi`, `didn't`/`didnt` and
`It's`/`its` all match. A transcript is scored to find out whether a note can be written from it, and
counting punctuation would measure the wrong thing.

It deliberately does **not** expand numbers or abbreviations. `2` and `two`, `443` and `four four three`
score as errors, because a technician reading back a serial number or a port is exactly where that
difference matters.

WER can exceed 100%: an engine that emits noise onto a short reference is worse than one that emits
nothing, and a definition that capped the number would hide that.

## What is still missing for ST-027

`research/fixtures/audio/` is empty. The WER number needs a real ten-minute narration recorded with a
microphone, plus its reference text — neither can be generated. Until then this script is tested against
synthetic pairs (`tests/test_wer.py`) and the criterion stays open.

## `draft_quality.py`

```
python research/eval/draft_quality.py research/fixtures/handcrafted
python research/eval/draft_quality.py <corpus> --json > report.json
python research/eval/draft_quality.py <corpus> --max-edit-ratio 0.25
python research/eval/draft_quality.py <corpus> --baseline report.json
```

ST-062's three numbers, with the definitions this repository uses.

**Edit ratio is words changed over words drafted**, from the Levenshtein alignment of the draft's steps
against the published note's. Word level rather than step level: rewriting three words of a twelve-word
step is not the same work as rewriting the step, and a ratio that treats them alike cannot hold the 25%
target. Case, punctuation and whitespace are folded first, for the reason `wer.py` folds them — a full
stop the technician added is not an edit worth counting against a model.

**A hallucination is a step whose evidence does not exist**: a `frame_refs` or `transcript_refs` entry
naming a frame or segment the session does not contain. Not "a step that reads oddly" — that is the
rubric's job, and a human's. **A step citing nothing at all is unsupported, not invented**: narration
without a screenshot is ordinary (ST-076), and counting it as an invention would make the number
meaningless on real sessions. It is reported separately, so a prompt that stops citing anything is
still visible.

**The gate** (`--baseline`) fails a run whose edit ratio is more than 10% worse than the baseline's, or
which hallucinates even one step more. A corpus report is JSON, so yesterday's run is tomorrow's
baseline.

A session directory holds `session.json`. A reference note, once someone has written one, lives beside
it as `reference.json` with the draft's shape plus an optional `rubric` object of human scores
(accuracy, completeness, tone). With no reference a session is scored against itself, which measures
nothing and says so: the point of running it today is that the harness is right before the corpus
exists. **The corpus is ST-030's** — ten real labelled sessions, the owner's to record.
