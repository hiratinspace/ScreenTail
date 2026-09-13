# Evaluation harness

| Script | Measures | Ticket |
|---|---|---|
| `wer.py` | Word error rate between a reference narration and what the speech pipeline produced | ST-027 |
| — | Redaction recall and false-positive rate against the labelled corpus | ST-042, needs ST-030 |
| — | Draft quality: edit distance, hallucination count, rubric | ST-062 |

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
