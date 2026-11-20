# The sized encoder context on real speech (ST-031)

**Measured 2026-11-19, of `main` at `c2311e58`.** `python research/eval/real_speech/measure.py` ran the
service's speech pipeline over each recording twice: with whisper.cpp's full thirty-second encoder window
(what ships), and with `SCREENTRAIL_SPEECH_SIZED_CONTEXT=1`. Model `base.en`, one thread, Whisper.net
1.9.1, scored by `research/eval/wer.py`.

## The short answer

**On real speech the sized context hears more words that were not said. It should stay off as it is
built.**

- Where one person talks clearly into their own microphone it costs little: 14.5% to 14.9% for the
  meeting's main speaker, 3.8% to 4.5% on read-aloud speech.
- Where the microphone mostly picks up quiet or other people's speech it is much worse: 46.9% to 81.5%,
  65.5% to 84.8% and 41.3% to 49.4% for the other three people in the meeting.
- Nearly all of the difference is inserted words: 485 to 735 over the meeting, 17 to 40 on read-aloud
  speech. Substitutions and deletions barely move.

## The numbers

| Recording | Length | Words said | Word error, full | Word error, sized | Sized against full |
|---|---|---|---|---|---|
| AMI ES2004a, speaker A | 17.5 min | 303 | 46.9% (47 sub, 21 del, 74 ins) | 81.5% (61 sub, 11 del, 175 ins) | 42.4% |
| AMI ES2004a, speaker B | 17.5 min | 1,067 | 14.5% (59 sub, 77 del, 19 ins) | 14.9% (71 sub, 65 del, 23 ins) | 5.9% |
| AMI ES2004a, speaker C | 17.5 min | 440 | 65.5% (81 sub, 45 del, 162 ins) | 84.8% (76 sub, 50 del, 247 ins) | 35.2% |
| AMI ES2004a, speaker D | 17.5 min | 804 | 41.3% (62 sub, 40 del, 230 ins) | 49.4% (65 sub, 42 del, 290 ins) | 16.2% |
| **AMI, all four** | 70 min | 2,614 | **35.1%** (249 sub, 183 del, 485 ins) | **45.0%** (273 sub, 168 del, 735 ins) | |
| LibriSpeech test-clean | 14.9 min | 2,017 | **3.8%** (52 sub, 8 del, 17 ins) | **4.5%** (46 sub, 5 del, 40 ins) | 1.7% |

"Sized against full" is the word error of the sized transcript scored against the full one, which says
how far apart the two are whatever the reference says. The two corpora are never pooled.

What the pipeline did with the same audio, both ways:

| Recording | Stretches heard | Segments kept, full | sized | Rejected as invented, full | sized |
|---|---|---|---|---|---|
| Speaker A | 147 | 64 | 73 | 64 | 44 |
| Speaker B | 136 | 107 | 115 | 15 | 10 |
| Speaker C | 234 | 139 | 131 | 69 | 44 |
| Speaker D | 225 | 113 | 134 | 91 | 71 |
| LibriSpeech | 87 | 87 | 87 | 0 | 0 |

## What the extra words are

Read side by side, the sized transcripts differ from the full ones in three ways:

- **A sentence said once is written several times over.** One remark of speaker A's came back as the
  same sentence six times in one segment.
- **A word is added after the end of what was said**, a near-rhyme of the last word, in a read-aloud
  sentence that the full window heard exactly.
- **A stretch the full window called blank comes back as words.** With the full window whisper.cpp marks
  quiet stretches as blank audio or inaudible, which the hallucination filter then rejects; with the
  sized window the same stretches come back as short phrases, and fewer are rejected (the table above).

The first two are what whisper.cpp is known to do when the encoder is given less than the window it was
trained on: the decoder does not find the end of the speech and carries on. The third matters for a
technician more than a meeting's numbers suggest: those quiet stretches are other people's voices
reaching the headset, and for a technician that is the end user on the call, whose voice ScreenTrail does
not record (INV-9).

## What ran, and where

- **The pipeline is the service's.** `research/eval/real_speech/harness` compiles the service's
  `WhisperRecogniser.cs`, `WavFile.cs` and `Transcribe.cs`, unchanged, against the same Core and the same
  Whisper.net. The detector, the gate, the sizes (512 and 1,024 positions) and the hallucination filter
  are the ones a session uses.
- **On a Mac, in a Linux container** (Apple M5, Docker, linux-arm64, `mcr.microsoft.com/dotnet/runtime:10.0`
  with the OpenMP runtime). whisper.cpp ran on the processor alone, as it does on Windows. It was not run
  on the laptop: the owner's demo had it that day.
- **AMI Meeting Corpus**, meeting ES2004a (one of the corpus's evaluation meetings): the four individual
  headset recordings, each scored against the manual word-level transcript (release 1.6.2) of the person
  wearing it, hesitations and false starts included. CC BY 4.0, https://groups.inf.ed.ac.uk/ami/corpus/.
- **LibriSpeech** test-clean: one whole chapter from each of the first two readers in the archive's order
  (readers 1089 and 1188), utterances joined with a second and a half of silence. CC BY 4.0, https://www.openslr.org/12.
- Neither corpus, and no transcript, is in the repository.

## What this does not show

- **It is not ST-027's criterion**, and ST-027 AC1 is still open. No technician narrated a support
  session into their own microphone here. The owner's recording is still the measure of that.
- **The absolute numbers are a meeting's.** Four people at one table reach each other's headsets, the
  reference keeps every "um", and "blank audio" printed by the engine counts as two inserted words. 35%
  is not what a technician alone at a desk would see; speaker B's 14.5% and LibriSpeech's 3.8% are
  nearer. The comparison between the columns is what the measurement is for.
- **One meeting, four voices, 2,614 words**, and two readers of read-aloud speech. Enough to see the
  direction and the kind of error; not enough to put a decimal on it.
- **Processor cost was not measured.** The ten runs shared the machine five at a time, on a different
  processor from the laptop's. Each sized run finished sooner than its full run (between a third and two
  thirds of the time), which agrees with the laptop's figures in `docs/dev/performance.md` and adds
  nothing to them.
- **An ARM processor, not the laptop's.** The arithmetic is whisper.cpp's on both, but not bit for bit.
  The full-window transcript of speaker B was word for word the same on the Mac's graphics processor and
  in the container, so the result is not a property of one backend.
- **Nothing was tuned.** A wider margin, larger sizes or a check for a repeated sentence might recover
  the loss. None was tried: this measures the switch as it is built.
