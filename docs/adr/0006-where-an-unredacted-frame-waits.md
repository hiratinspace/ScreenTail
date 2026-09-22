# ADR-0006: Where an unredacted frame waits

- **Status:** Accepted 2026-09-22 (ST-041, ST-025).
- **Invariant:** INV-1.
- **Supersedes:** nothing. ADR-0004 decides what happens to a frame nobody could read; this decides where
  a frame waits to be read.

## Context

A screenshot is taken, encoded as JPEG, and written into the encrypted store with
`redaction_pending = 1`. The redaction worker reads it back, OCRs it, masks what the patterns found,
downscales the result and writes it over the original with the flag cleared.

So the unredacted frame is on disk for as long as it takes the worker to reach it. That is usually
milliseconds — redaction measured a median of 171 ms a frame, and a technician generates a few frames a
minute — and it is longer whenever the backlog is not empty.

**This is not an INV-1 breach, and the ADR should not pretend it is.** INV-1 says no unredacted frame is
shown, exported or transmitted. A row in a SQLCipher database is none of those three: the read path
filters `redaction_pending` everywhere, the bundle builder refuses pending frames, and Review cannot see
them. The file is encrypted at rest with a DPAPI-protected key.

What it is instead is a window, and three things can reach into it:

1. **Anything holding the store key while the frame is pending.** That is the technician's own account,
   which is also the threat model the IPC peer check exists for: a same-user process is the ordinary
   attacker here, not an exotic one.
2. **A crash.** `RecoverAsync` purges pending frames on the next start, which is the right answer — but
   the bytes are on disk from the crash until the service runs again, which may be days.
3. **Freed pages.** SQLite does not overwrite a deleted row's pages, and `secure_delete` is off, so the
   ciphertext of an unredacted frame survives its deletion until the page is reused. Retention's
   `VACUUM` now runs only when a third of the file is free (#120), so that can be a while.

None of these is a hole in the invariant. Together they are a reason to ask whether the frame needs to be
there at all.

## It does not need to be there

The store is not buying durability. `RecoverAsync` **purges** pending frames rather than resuming them,
because nobody has read them and ADR-0004 says a frame nobody has read cannot be stored. A staged frame
that survives a crash is deleted by the thing that finds it.

So what the store is actually providing for a pending frame is a **buffer**: somewhere to put frames when
capture is faster than redaction. That is a real service and the reason this is not a one-line change.

## Decision

**An unredacted frame waits in memory, and reaches the store only after it has been redacted.**

`StageFrameAsync` stops being where a captured frame goes. The capture loops hand the encoded JPEG to a
bounded in-memory queue; the redaction worker takes from that queue instead of polling
`TakeNextPendingFrameAsync`; and `MarkFrameRedactedAsync` becomes the insert rather than an update.

Consequences worth stating plainly:

- **Unredacted pixels never touch disk.** All three windows above close, not because the enforcement got
  stricter but because there is nothing to enforce against.
- **The write disappears rather than moving.** Today each frame is written twice — 200 KB to 1.5 MB
  staged, then the downscaled redaction over it. The staged write goes, so it is roughly 3.5 MB of disk
  per frame down to about 0.4 MB.
- **The `redaction_pending` column stops having a `1` state in practice.** It stays, because a store
  written by an older build may hold pending rows and recovery must still purge them.

## What happens when the queue is full

This is the question the old design answered by accident, and it has to be answered on purpose.

Disk absorbed bursts. Memory cannot: a queue of encoded frames at 1.5 MB each is a few megabytes at a
depth of three and a hundred megabytes at a depth of seventy. ST-031 budgets 600 MB for the whole
application and speech already claims 505 MB of it.

**The queue is bounded at 4, and a capture that finds it full is dropped and counted.**

Dropping is the honest answer of the three available:

- *Blocking the capture loop* would make the 50 ms drain wait on OCR, which is the bug fixed in #124 in
  a different place.
- *Falling back to the store* would keep the window this ADR exists to close, and would keep it for
  exactly the busy sessions where it is widest.
- *Dropping* loses a screenshot and says so. `frames_purged_unredacted` already exists, already reaches
  the bundle, and already makes the draft hedge rather than claim to have seen everything — because
  ADR-0004 needed the same admission for a different reason.

Four is chosen to be small. Redaction is 171 ms a frame and clicks arrive a few a minute, so a depth
above one means redaction is already losing; the queue is there to absorb a scene change landing on a
click, not to hold a session.

## What this does not change

**The double decode stays.** The recogniser decodes the JPEG to read it and the masker decodes it again
to paint on it, and the obvious fix — pass the decoded bitmap between them — costs about 33 MB per frame
held instead of 1.5 MB. That is a different trade with a different answer, it is an efficiency change
rather than a privacy one, and putting it in this ADR would let a memory decision ride in on a privacy
decision. It stays in the efficiency backlog.

## Alternatives considered

**Turn on `secure_delete` and keep staging.** One pragma, closes the third window and neither of the
other two, and makes every delete in the store slower for the life of the product.

**Encrypt staged frames a second time with an in-memory key.** Closes the same windows as this ADR while
keeping the buffer, at the cost of a second key nobody can recover and a scheme somebody would have to
review. More machinery than not writing the bytes.

**Do nothing.** Defensible: it is not a breach, and the window is short. It is rejected because the
window is not short *when it matters* — a crash holds it open for days, and the busiest sessions hold it
open longest.

## Consequences

- `ISessionStore.StageFrameAsync` and `TakeNextPendingFrameAsync` lose their callers in the running
  service and are kept only for the recovery path and for tests that exercise it.
- The redaction worker's backlog count becomes a queue depth rather than a query, which the HUD reads
  the same way.
- A session recorded on a machine where redaction cannot keep up produces fewer frames than one on a
  fast machine, and says so in the note. That is visible behaviour and is the point.
- Verification is on the laptop: frames dropped under a synthetic burst, and the store observed to hold
  no row with `redaction_pending = 1` at any moment during a session.
