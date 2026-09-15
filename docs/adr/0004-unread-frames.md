# ADR-0004: A frame the recogniser read nothing from is discarded

- **Status:** Accepted 2026-09-15 (ST-048).
- **Supersedes:** the reading recorded in ST-041's implementation, that a frame with no text is stored.
- **Invariant:** INV-1.

## Context

ST-041 left one question open, and `docs/review/weaknesses.md` P0-1 turned it into a ship-blocker.

The redaction worker is the only thing allowed to read a staged frame and the only thing allowed to clear
`redaction_pending`. INV-1 reduces to a single claim about it: a frame it marks readable has had its
secrets painted over first.

It was not true. The confidence gate read:

```csharp
if (!text.IsEmpty && text.MeanConfidence < _options.MinimumConfidence)
```

When the recogniser returned **zero words**, `!text.IsEmpty` was false, so the discard was skipped. There
were no regions to paint, the masker early-returned, and the original screenshot was re-encoded unchanged
and written with `redaction_pending = 0`. It was then visible to Review, to the bundle builder and to
publish. The comment two lines above said a frame the recogniser could not make out is where a secret is
most likely to survive; the guard exempted precisely that case.

The reasoning behind the original behaviour was that a screenshot with no text — a wallpaper, a progress
bar — is not suspicious, it is just quiet. That is true of the frames it had in mind and false of the
ones that actually arrive, because **nothing downstream can tell the two apart.**
`Windows.Media.Ocr` returns zero words for all of these:

- a blank desktop, which is the harmless case;
- a dark-mode terminal, or any theme whose contrast the engine cannot segment;
- a page in a language with no pack installed — `TryCreateFromUserProfileLanguages()` falls back to
  `en-US`, and a Cyrillic or CJK screen then reads as nothing at all;
- a frame it decoded and could not segment, which ADR-0001 finding 9 already records: the canonical test
  card `4111 1111 1111 1111` comes back empty, because a long run of one ambiguous glyph defeats it.

The second defence did not exist either. `WindowsOcrRecogniser` always reports
`UnknownConfidence = 1.0`, so the confidence gate beneath the empty check can never fire on the only
platform that ships. Two independent defences, both inert.

## Decision

**Zero words is treated as unreadable, not as clean. The frame is discarded.**

A frame that genuinely had no text on it is lost along with the rest. We accept that cost.

The two discard reasons are counted apart — `RedactionProgress.Unread` for "the engine read nothing",
`Unreadable` for "the engine read something unusable" — because they mean opposite things and one number
covering both would hide whichever is happening. A count climbing on a machine that is plainly showing
text is how a missing language pack announces itself, and there is currently nothing else that would say
so.

## Why this way

**The wrong guess is not symmetrical.** Keeping an unread frame publishes a customer's screen into a
ticket note or a Hudu article with nothing having checked it, and INV-1 is the claim the product is sold
on. Dropping a readable frame costs one screenshot out of a session that takes one per click, and the
session already counts and displays what it lost: Review says "3 screenshots were removed because they
could not be redacted", so the gap is visible rather than silent (Spec §5 S3).

**There is no third option.** A frame kept "just in case" is an unredacted frame. Storing it behind a
flag that Review honours would put the decision in every future reader instead of in one place.

**Distinguishing the cases needs evidence we do not have.** An image-complexity heuristic — "this frame
has too much structure to be genuinely blank" — is the obvious alternative and was rejected: it is a
second detector with its own false-negative rate sitting in front of INV-1, and we have no corpus to
measure it against. ST-030's labelled captures are what would make that measurable. If the `Unread`
count turns out to be large in real sessions, this ADR is where to revisit it, with numbers.

## Consequences

- Sessions on machines with an OCR language mismatch will lose most of their frames, loudly, in the
  `Unread` count, rather than storing them unchecked. That is the intended failure.
- ST-021's capability probe should grow an OCR-language check so the condition is caught at start rather
  than diagnosed from a counter. Not in ST-048; noted here as the follow-up.
- The diagnostics panel (ST-085) should show `Unread` beside `Unreadable`. A technician who sees frames
  going missing needs to be told which of the two is happening.
- ST-060's bundle builder already sets `frames_purged_unredacted`; discarded frames continue to flow
  into that count, so a draft says how much of the session it could not see.

## Alternatives rejected

| Option | Why not |
|---|---|
| Keep storing empty-OCR frames (the behaviour this replaces) | Breaks INV-1 for every case where the engine failed rather than the screen being blank, and those cases are indistinguishable at this layer. |
| Store them with `redaction_pending` left set | Finalize purges pending frames, so this is the same outcome by a longer route, and it leaves a window where a reader that forgets the filter sees raw pixels. |
| Fix the confidence signal instead, and rely on it | Worth doing and not sufficient. A real confidence number from `Windows.Media.Ocr` would help with *badly* read frames; a frame with no words has no confidence to report. |
| An image-complexity heuristic to spot genuinely blank frames | A second detector in front of INV-1 with no corpus to measure it against. Revisit with ST-030's data. |
