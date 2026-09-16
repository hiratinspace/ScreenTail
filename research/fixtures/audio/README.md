# Audio fixtures (ST-027)

**Status: empty, and it needs a person with a microphone.** The whole speech pipeline is built as of
2026-09-16 — microphone capture, voice-activity detection, the gate, local transcription and the
hallucination filter — and this is the one acceptance criterion that cannot be synthesised.

When the recording lands, one command measures it. Nothing else is waiting.

> ST-027 AC1 — *10-minute narrated test → WER ≤ 15% default model*

## Why it cannot be faked

A generated or text-to-speech recording would measure the wrong thing twice over. It has none of the
room tone, keyboard noise, held breath or mid-sentence restarts that a real support call has, so a WER
scored against it would be optimistic in exactly the conditions that matter. And the pipeline's gating
thresholds — `SpeechGate`'s hysteresis, hangover and minimum length — were chosen for a human pausing
between clauses, which synthetic speech does not do.

## What to record

**One take, about ten minutes, of you narrating a support session as you would if a colleague were
listening.** Not read aloud from a script: the errors that matter are the ones a technician makes while
thinking, and reading produces a fluency the model finds far easier than real speech.

Cover the ordinary vocabulary, because that is where word error rate turns into a wrong note:

- Product and tool names — ScreenConnect, Hudu, ConnectWise, Outlook, Windows, the print spooler.
- Things read back aloud — a ticket number, a port, part of a serial. `wer.py` deliberately does **not**
  fold `443` into `four four three`, because a technician reading a number back is precisely where a
  transcription error becomes a wrong fact in a ticket.
- At least one stretch of silence of twenty seconds or more, and one of typing with no speech. These are
  the conditions Whisper invents text in, and `TranscriptAssembler`'s hallucination filter is written
  against exactly this — a recording with no silence in it cannot test the thing most likely to go wrong.
- A couple of genuine self-corrections: "so I restarted the — actually first I checked the service".

**Do not say anything you would mind reading back in a public repository.** No real customer names, no
real ticket numbers, no credentials. This file is committed.

## How to save it

```
research/fixtures/audio/
  narration-01.wav        16 kHz mono PCM — what the pipeline consumes
  narration-01.txt        what you actually said, typed out
```

The `.txt` is the reference and it has to be **what was said, not what was meant**. Include the false
starts and the "um"s in the places they happened. A reference that tidies up the speech scores the
pipeline against a recording that does not exist, and will report a WER better than the truth.

Convert from whatever your recorder produced:

```
ffmpeg -i narration-01.m4a -ac 1 -ar 16000 -c:a pcm_s16le narration-01.wav
```

## Then

On Windows, run the recording through the real pipeline — the same detector, gate, model and
hallucination filter a session uses — and keep what it heard:

```powershell
dotnet run --project client/ScreenTail.Service -c Release -- `
    --transcribe research/fixtures/audio/narration-01.wav > heard.txt
```

The first run downloads the model, which is about 150 MB and hash-checked. It also prints, to standard
error, how long it took against how long the recording was: under 1.00x real time is ST-027's lag
criterion (AC4) met on that machine.

Then score it:

```
python research/eval/wer.py \
    research/fixtures/audio/narration-01.txt \
    heard.txt \
    --budget 0.15
```

It exits non-zero above the budget, so once the audio exists this becomes a CI gate rather than a number
somebody reads once.

## What this unblocks

ST-027's first acceptance criterion, and with it the honest closing of ST-028, ST-030, ST-080 and ST-123,
all of which sit behind it.
