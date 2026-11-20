# Performance budgets (ST-031)

ST-031 sets ScreenTrail's budgets: idle CPU under 1%, recording CPU under 15% of a four-core machine,
memory under 600 MB with speech, and disk under 40 MB for a twenty-minute session. This page says how
each one is measured, whether CI enforces it, and the latest numbers from the test laptop (SCREENTRAIL,
Windows 11, 8 logical processors).

## The budgets

**Two units, side by side.** The tests measure processor time as a percentage of **one core**, and the
throttle and Task Manager read it as a percentage of **the machine**. The laptop has 8 logical
processors (4 cores, 8 threads), so 8% of one core is 1% of the machine. ST-031's recording budget, 15% of
a four-core machine, is 60% of one core: **7.5% of this laptop**. Until 2026-11-17 this table gave the
recording figure in one unit only (4.7% of one core, 0.6% of the machine), and M1's "25%" was read off the
machine. Every processor figure below gives both.

| Processor budget | Measured by | Enforced | % of one core | % of the machine (8 logical processors) |
|---|---|---|---|---|
| Idle, service, under 1% | `ServiceCostTests.WithNoSessionTheServiceIsAlmostSilent` | Yes, on the laptop, against 1% of **one core** (0.125% of the machine) | 0.41 net (run 40626688381) | 0.05 |
| Idle, UI, under 1% | `ShellCostTests.WithNoSessionTheShellIsAlmostSilent` | Yes, on the laptop, against 1% of one core | 0.0 net (0.22 gross, the test host included) | 0.0 |
| Recording without speech, under 60% of one core | `ServiceCostTests.ARecordingSessionStaysInsideItsProcessorAndDiskBudgets` | Yes, on the laptop | 12.9 (run 44027063937, the window filling the screen; it read 4.7 on a 420x220 window before) | 1.6 |
| Recording, technician narrating (synthetic voice), as shipped | `RecordingCostBreakdownTests.ANarratedRecordingStaysUnderItsCeiling` | Yes, on the laptop, against a ceiling of **120% of one core** (15% of the machine), not the budget: see "Narrated recording" below | 95.9 (run 44027063937); 105.2 before whisper moved to one thread | 12.0; one-second readings peak 20.9 (33.8 before) |
| The same with the sized encoder context (switched off) | the same test | Reported only | 42.1 | 5.3; peak 20.6 |

The other budgets, from laptop run 40626688381 (2026-10-28, commit 9f4113d) unless the row says
otherwise:

| Budget | Measured by | Enforced | Latest on the laptop |
|---|---|---|---|
| Idle wakeups, service | the same test | Yes, on the laptop, a ceiling of 40 a second (set from the laptop, not named by ST-031) | 17 a second net (14 to 17 across four runs), down from 129 |
| Idle wakeups, service, on a busy desktop | `ServiceCostTests.WithNoSessionABusyDesktopDoesNotWakeTheService`, `IdleHookTests` | Each watcher's pump thread under 10 a second, everywhere (hosted runner included); the whole service under the same ceiling of 40, on the laptop | Hosted: 0.0 a second for each pump with 68 renames a second behind the window in front. Laptop (41349604369): pumps 2.3 and 0.0, the service 25.0 net |
| Idle wakeups, UI | the shell test | Reported only | 1.4 a second net (the pill reports every 2 s) |
| Disk under 40 MB per 20 minutes | the same test | Yes, on the laptop: the measured frame size times the 85-frame session `SessionStoreTests` assumes | 70 KB a stored frame, 5.8 MB for 85 frames. At the test's own rate of one text frame every 6 s it is 1.5 MB per session-minute, about 30 MB per 20 minutes; that figure is reported, not enforced |
| Per-key and per-click hook cost under 1 ms | `InputHookTests` (laptop input job) | Yes, since ST-024 | Worst callback 145 µs typing 860 keys, 0.9 µs per click |
| Memory after the speech model's idle unload | `SpeechUnloadTests` | Reported only (first measurement) | base.en adds 150 MB loaded; the unload gives back 158 MB. Run 44027063937 read 947 MB before loading and so no sensible difference: the test before it (a ten-minute transcription abandoned at the stop's bound) was still releasing. Measured from no model held in the process since 2026-11-17: 94.1 MB before, +108.9 MB loaded and used once, 152.2 MB given back (run 44139012468); see "What a stop waits for" |
| Memory under 600 MB with speech, whole process | `scripts/windows/measure-idle.ps1`, run by hand | No | Not run yet; ADR-0001 measured 505 MB with continuous transcription |

Idle CPU is held against one core rather than the machine, which is stricter than the budget: 1% of
the laptop's eight processors is 8% of one core, which nothing at idle would ever reach, so it could
not catch a regression that matters.

Wakeups: two laptop runs of the same code measured 17 and 14 a second net, with slices from 15 to 21,
so a ceiling of 40 is enforced. It leaves room for that spread and still fails a ten-a-second poll
coming back. It may not catch a single new loop at four or five a second, which would land inside the
spread; the Mac suite's counting-clock tests (`IdleWakeupTests`, `SessionLoopTests`) hold each loop to
making no timer at idle, and this is the whole-service check behind them. The UI's figure is within the host's own noise (one to five a second either way), so it is
reported, not enforced.

## What was fixed

A code read found the service waking about forty times a second with no session, each time to find
there was nothing to do. The first laptop run measured it at **129 wakeups a second and 2.5% of one
core** (median slice, clock ticks), over the 1% budget, and that run failed the job as it should.

| Loop | Before, with no session | Now |
|---|---|---|
| Click drain (`ClickCaptureLoop`) | 20 a second, empty buffer | A timer only while a session is under way |
| Redaction workers (`RedactionWorker`) | 2 workers, 5 a second each | Wait for a frame to arrive; back off only after a failure |
| Sensitive-context guard | 4 a second | Only while a session is under way; its first turn comes as the session starts |
| Scene sampler | 1 a second | Only while a session is under way |
| Password-field guard | 1 a second backstop | Backstop only during a session or a hold; focus changes still wake it |
| Narration recorder | 2 a second backstop | Every 5 s; the nudge on each state change is the mechanism |

`SessionLoop` (Core) is the shared rule: a timer exists only in the states the input hooks are in
(recording, paused, suppressed, finalizing), the step runs at once when a session begins and once more
after it ends, and a test ends a session inside a turn on a frozen clock to hold it to that last turn.
Nothing on the hook path changed.

The guards now hear that a session started from `StateChanged` alone, so the state machine tells each
listener on its own: one that throws is counted and the rest are still told, instead of a multicast
invoke stopping at the fault and leaving a guard asleep for the session. The password-field guard's
one-second poll still runs during a session and while a hold is in place; two tests flip the field with
no focus event and require the poll to suppress and to release.

The review also found a pre-existing fault: a cancellation nobody asked for (a call timing out) ended a
redaction worker for the life of the service. It now costs that frame, let go of unread, and the worker
carries on.

| Laptop run | Service idle wakeups (net) | Service idle CPU, median slice |
|---|---|---|
| 40585899774, before | 129 a second | 2.5% of one core by clock ticks (failed) |
| 40587029859, loops fixed | 25 a second | 0.94% by clock ticks, which is three ticks |
| 40588642451, narration too | 17 a second | 0.32% net by cycles (0.94% by clock ticks) |
| 40623029883, same code again | 14 a second | 0.36% net by cycles (0.62% by clock ticks) |
| 40623805201, with the ceiling | 16 a second | 0.33% net by cycles (0.31% by clock ticks) |
| 40626688381, after the review fixes | 17 a second | 0.41% net by cycles |

## What still wakes at idle, and why

| What | How often | Why it stays |
|---|---|---|
| Indicator guard | 2 a second | INV-4: capture stops within its grace when nothing on screen says it is on. Left unchanged on purpose |
| Redaction backlog report | 1 a second | ST-041 reports the depth every second, and `RedactionThroughputTests` holds it to that |
| Foreground watcher's safety net | Every 2 s | Catches a title change a hook missed |
| Scope coordinator | Every 5 s | Re-decides the window in front after a policy change |
| Narration recorder backstop | Every 5 s | Covers a host that never nudges it |
| UI pill report to the service | Every 2 s | INV-4: the service asks for it before starting a session, and it is sent from the UI thread so a frozen UI stops sending it |
| Outbox, retention | 30 s, hourly | Minutes-scale work nobody waits on |

The Review pane's two quarter-second timers run only while Review is on screen.

## The idle hooks, and a figure that followed the desktop (2026-11-01)

On 2026-11-01 the idle service measured 17 to 20 wakeups a second in some laptop runs and 47 to 52 in
others, on the same code a few minutes apart (41326764559 passed at 16:38, 41308671440 failed at 16:50;
main failed in 41310040373). The failing runs had one thread taking the difference: "the busiest 29.4,
6.2, 5.2/s" against "6.3, 4.4, 3.0/s". The pool threads did not rise with it, so whatever woke that
thread handed nothing on.

The service keeps two WinEvent hooks with no session, and both heard every thread on the desktop:

- **Foreground watcher, `EVENT_OBJECT_NAMECHANGE`.** Any window anywhere renaming itself (a progress
  bar, a clock, a list refreshing) woke the watcher's pump, which threw the event away because the
  window was not the one in front. That matches the single busy thread: the callback filters and hands
  nothing on.
- **Focus watcher, `EVENT_OBJECT_FOCUS`.** Every control on the machine taking focus woke the pump and
  then the password-field guard, which saw no session and went back to sleep. On the hosted runner, focus
  moving between two fields a hundred times a second woke the pump 157 times a second.

What changed:

- The name-change hook now listens to **the foreground window's thread only** and moves when the
  foreground moves (`ForegroundNameHook`, Core). A window's own retitle is raised on the thread that owns
  it, so the watcher hears every title change it used to act on. If Windows refuses the narrow hook it
  falls back to every thread, the old subscription, and never to the poll alone; it tries the narrow
  hook again at the next foreground change and at every poll, and counts the refusals
  (`WindowsForegroundWatcher.NameHookRefusals`). Since contract v8 the count is in the diagnostics
  reply (`name_hook_refusals`) and the "What's being captured right now?" panel shows it as "Hook
  fallbacks", with what it means beside a count above 0, so a refusal that costs idle wakeups says so.
- **A classic console in front is heard over every thread** (review of #252). conhost draws a
  `ConsoleWindowClass` window and raises its renames, but Windows names the shell running in it as the
  window's owner, so the narrowed hook followed a thread that never renames anything: with the hook
  narrowed, a classic console retitling itself in front was reported **0** times in 5 s on both the hosted
  runner (41347969714) and the laptop (41347969730), and its title would have waited for the 2 s poll.
  While such a window is in front the hook hears every thread, as it always did; that costs what the old
  hook cost, for as long as a classic console holds the foreground. Windows Terminal draws its own window
  and is followed on its own thread (`IdleHookTests.AConsoleInFrontRetitlingItselfIsReportedWithoutThePoll`
  checks both against a hook of the test's own).
- The focus hook is **in only while a session is under way or a password-field hold is kept**
  (`FocusInterest`, Core, and `WindowsFocusWatcher.Listen`). At a session's start the guard asks what has
  focus on the transition itself, as before, and its one-second backstop covers a focus move in the
  moment before the hook is back. `FocusInterest` subscribes to the session before it reads it, so a
  session starting while the service is still putting it together is not missed.
- `EVENT_SYSTEM_FOREGROUND` still hears every thread: auto-start needs to know when a remote tool comes
  to the front, and the foreground moving is rare.

The probe that pinned it down ran on the hosted runner (temporary, deleted). Windows Terminal, which holds
the hosted runner's foreground, raised **no** WinEvents in the system, console or object ranges even
while printing flat out, so a busy console in front does not reach either hook. What a console printing
flat out does do is keep a core or two busy, and on a four-core runner that multiplied this process's
.NET pool threads' context switches about fivefold (24 a second to 127): preemptions of threads already
running, not wakeups anything asked for. The busy-desktop test therefore paces its console at a line
every 20 ms. A window behind renaming itself 74 times a second reached a hook over every thread every
time and woke the narrowed foreground pump 4 times a second, its own poll included.

The idle reports now name the busiest threads by their own description (`.NET TP Worker`,
`ScreenTrail foreground watcher`, never a window title) and give each watcher pump's rate. The first laptop
run with the narrowed hooks (41347969730, before the console fix) measured the quiet idle service at
**22.4 wakeups a second** net, the foreground watcher's pump at 0.6 and the focus watcher's at 0.0, the
busiest threads the .NET timer (8.3), pool gate (4.5) and a pool worker (4.2); nothing near the 29.4 a
second one thread took in the failing runs. On the busy desktop it measured 28.1 net, the pumps 2.0 and
0.0. The run with the console fix (41349604369) measured 21.7 net at a quiet idle (pumps 0.7 and 0.0;
busiest the timer 8.3, pool gate 5.6, a pool worker 4.2) and 25.0 on the busy desktop (pumps 2.3 and
0.0); a classic console in front had all 5 of its announced titles reported, Windows Terminal all 4.
Neither run can prove the laptop's busy desktop of the failing runs was back, so the evidence for the
cause is the shape of the failure (one thread, nothing handed on) and the hosted measurements above.
The laptop's run on the pull request's final head (#252, 41363089926) measured 21.9 net at a quiet idle
(pumps 0.7 and 0.0; busiest the timer 7.8, pool gate 6.3, a pool worker 4.1) and 23.2 on the busy
desktop (pumps 2.4 and 0.0), both under the ceiling of 40.

## Over budget: what gives first (AC2)

Built 2026-10-29 (`client/ScreenTrail.Core/Perf`). While a session is under way the service reads its own
processor time once a second, as a share of the whole machine (process processor time over wall time,
divided by the logical processors), and compares it with the recording budget in `CpuBudget`: 15% of a
four-core machine. A machine with more cores is held to the same amount of processor, 60% of one core
(7.5% of the laptop's eight processors), the figure the laptop check above enforces; one with fewer is
held to 15% of itself.

| After | Level | What changes |
|---|---|---|
| 10 s over budget | Fewer samples | The scene sampler looks at the screen every other tick (every 2 s by default) |
| 10 s more | Fewest samples | Every fourth tick (every 4 s) |
| 10 s more | OCR one at a time | And redaction reads one frame at a time instead of two while the session records |
| 10 s more | OCR clicks only | And the scene sampler stops looking, so OCR reads only the frames a click or a marked moment asked for |

- **Back up:** one step after 30 s below 80% of the budget. Between 80% and 100% the level holds, so a
  load that sits at the budget does not flap. A reading back under the budget starts the ten seconds again.
- **Never throttled:** the input hooks, click events and click screenshots, mark moment, and speech.
  They still count towards the reading, so a busy transcription is paid for out of sampling and OCR. A
  click's screenshot is never paused or skipped: it is always taken, and a frame that is waiting is read
  as soon as the reader is free. From "OCR one at a time" down, click frames are read one at a time like
  every other frame, so a burst waits its turn in the four-frame queue, and a burst that outruns a single
  reader can fill the queue and be refused and counted, as a burst that outruns two readers always could
  (ADR-0006). An earlier draft also paused 2 s after each frame at the deepest level, which refused bursts
  a reader could have kept up with; the 2026-10-29 review caught it and the pause is gone.
- **Redaction is untouched (INV-1).** A skipped look is a picture never taken. A frame waiting its turn
  waits in memory, never in the store, and is read, searched and painted exactly as at full capture.
- **Finalize is never stretched.** One frame at a time holds only while the session records. Paused or
  finalizing, both readers work, so the frames finalize waits for inside its twenty-second redaction grace
  are read as fast as they ever were.
- **Session end** puts capture back to full and forgets the readings. A discard, or a finalize shorter
  than a second, can be followed by a new session before the next reading; that reading sees a session it
  has not seen and forgets the last one first, so the new session starts at full. Until it comes (at most
  a second) the new session runs at the old level. With no session nothing is read and no timer exists,
  so the idle figures above do not move.
- **Log (INV-10):** one line per change, `Capture throttle Full -> FewerSamples at 9.3% of the machine`.
  Nothing else.
- **The login-screen check reacts later.** ST-041's OCR heuristic for a sign-in prompt sees a frame only
  once it is read. With one reader, a click's frame can wait behind the frames ahead of it in the queue: at
  most three, each taking a median of 171 ms and at most about 700 ms (ST-041's budget), so up to about
  two seconds later than at full capture. At the deepest level a prompt that appears without a click is
  not photographed, so the OCR check cannot see it until the next click; the password-field guard (ST-040),
  which asks Windows rather than reading the screen, is unaffected and still suppresses capture the moment
  focus lands in a password field.
- **Scene frames can stop for long stretches.** Speech is never throttled, so a technician narrating
  steadily can hold the service over budget for as long as they talk, and the throttle stays down for as
  long as it is. Down to "OCR clicks only", that session has no scene frames at all until the process has
  been well under budget for thirty seconds a step; above it, scenes are looked at every 2 or 4 s. While
  throttled, ST-026's AC2 ("error dialog appears, frame within 2 s") is suspended: a dialog that appears
  without a click may be photographed late or not at all. Clicks and marked moments are still
  photographed throughout.

The Mac suite proves the rules on a fake processor clock and a hand-moved clock (`CaptureThrottleTests`,
`ThrottledSamplingTests`, `ThrottledRedactionTests`). The laptop rig composes the throttle as the host
does. The recording measurement reports how often the throttle moved and the level it ended at, and on
the laptop it fails if the throttle moved at all: a throttled service records less, and its figure would
hide the regression that throttled it. At 12.9% of one core (1.6% of the machine) without speech it does
not move. With a technician narrating it does: see "Narrated recording" below.

### Owner step on the laptop

Seen on the laptop in the test rig, not yet in the real service. With synthetic narration and the
throttle acting (run 44027063937) it stepped Full to FewerSamples at 24 s, FewestSamples at 39 s and OCR
one at a time at 51 s. During a demo rehearsal, with the service's console log in view:

1. Record a five-minute session while narrating throughout. Expect `Capture throttle` lines unless the
   sized encoder context is on. Note the levels and percentages they give and paste them here. Each
   percentage is one one-second reading of the machine, not the session's mean.
2. Run `scripts\windows\measure-idle.ps1 -Label "recording, speaking"` alongside, so the throttle's
   reading can be compared with what `Get-Process` sees.
3. If the narrated session trips the throttle, check how many scene frames it kept. The count is in
   the service's log at debug level (start the service with `SCREENTRAIL_LOG_LEVEL=Debug`; each kept frame
   logs `Scene frame N of M samples`). Compare it with a session of the same length without narration.
   A count near zero means narration alone holds the service over budget and costs the session its scene
   frames; paste both counts here, since that is a reason to revisit the budget or the order in which
   things give.

Without narration a session should not trip the throttle. With it, the rig says it will, until the
sized context is switched on or the throttle's accounting changes (ST-031's status).

## Narrated recording (M1, 2026-11-17)

M1 (2026-11-13) saw the capture service at about 25% of the laptop during a six-minute narrated AnyDesk
session, against a budget of 7.5% of the machine (60% of one core). 25% of the machine is about 200% of
one core. It was most likely one of the throttle's one-second readings, which it logs as `at N% of the
machine`: two whisper threads busy are 25% of eight processors. CI had never measured a narrated session:
the recording test has no speech, and its window was 420x220, so its scene frames were small crops.

`RecordingCostBreakdownTests` measures it on the laptop with the composed service (`ServiceRig`), a window
filling the screen, and narration from Windows' synthetic voices through a scripted microphone. It writes
numbers, thread descriptions and module names only, never an image, a title or the words. Its first run
was 44014246194 (main 074d4cfb plus the measurement); after the fixes, 44027063937.

### Where the processor goes, per unit of work

Processor milliseconds, net of the idle test process, run 44027063937 (run 44014246194 within a few
percent):

| Stage | Processor ms |
|---|---|
| Scene look (sampler, a 1920x1080 frame) | 12.7 |
| Frame capture at 1920x1080 | 16.8 |
| OCR, a dense 9 pt list, at up to 2x (ST-042) | 615 (438 as captured) |
| OCR, a settings dialog, at up to 2x | 200 (35 as captured) |
| Pattern search | 10.2 |
| Mask | 34.9 |

OCR at twice the size (#275, #278) is about 90% of a frame's redaction and runs on one thread. It is not
changed here: redaction stays complete, and a frame that cannot be read in time is dropped.

Speech dominates a narrated session. whisper.cpp encodes a full 30-second window (1500 audio context
positions) for every segment, so a segment costs nearly the same processor time whatever its length:

| Speech, base.en, per segment | Processor s | Wall s |
|---|---|---|
| Two threads, full context (before), 10-second clip | 6.7 | 3.4 |
| One thread, full context (as shipped) | 4.9 to 5.4 | about the same as the processor time (5.3 on the 10-second clip, first run) |
| One thread, context sized to the segment (switched off) | 1.2 to 3.3 | |
| Four threads, full context (first run) | 9.9 | |

### The composed service, narrating

| Session (45 s, the throttle reading but not acting unless said) | % of one core | % of the machine | One-second readings, % of the machine | Readings over 7.5% |
|---|---|---|---|---|
| Capture, the room quiet (30 s) | 21.6 | 2.7 | median 1.7, peak 9.2 | 4 of 30 |
| Capture and narration, two threads (before, run 44014246194) | 105.2 | 13.1 | median 13.6, peak 33.8 | 24 of 45 |
| Capture and narration, one thread (as shipped) | 95.9 | 12.0 | median 12.8, peak 20.9 | 37 of 45 |
| The same, sized context | 42.1 | 5.3 | median 4.0, peak 20.6 | 13 of 45 |
| As shipped, the throttle acting (75 s) | 90.8 | 11.3 | median 12.7, peak 21.0 | 60 of 75 |

Speech was 84 of the 105 points before (whisper's OpenMP threads 43.6, the transcription task 29.4,
short-lived threads about 10), OCR 14 to 18. One thread costs about a quarter less processor for a
segment and takes about twice as long; the throttle still trips, because 96% of one core is over the 60%
budget for most of a session. Acting, it stepped Full to FewerSamples at 24 s, FewestSamples at 39 s and
OCR one at a time at 51 s, and kept no scene frames in its window. With the sized context it would never
have moved. The ceiling the test enforces is 120% of one core (15% of the machine): the as-shipped figure
with about a quarter again as margin. It catches a stage that grows, not a return to two threads (105).

### What a stop waits for

One thread has a cost at the stop: `SpeechUnloadTests.AStopDoesNotWaitForATranscriptionInFlight`
measured the disposal at its 500 ms bound (501 and 508 ms, against 221 to 234 ms on two threads), the call
left running past the bound and reported. The stop itself is still bounded (ST-112). Why, and why it
cannot be fixed inside Whisper.net 1.9.1 (2026-11-17):

- **The token already reaches whisper.cpp.** Whisper.net 1.9.1 passes the token given to `ProcessAsync`
  to whisper.cpp's `abort_callback` and `encoder_begin_callback` (decompiled: `OnWhisperAbortStatic` and
  `OnEncoderBegin` read `IsCancellationRequested`), and `WhisperRecogniser` links the stop's token into
  it. Its enumerator throws at once; the native call runs on to whisper.cpp's next check, and the
  processor's disposal waits for that.
- **whisper.cpp reads the abort only between steps.** In the commit Whisper.net 1.9.1 pins (f24588a), and
  in master on 2026-11-17: before each 30-second window (`encoder_begin_callback`), after each whole
  encoder pass (the end of `whisper_encode_internal`) and after each decoder step. Not inside the mel
  spectrogram, which is computed for all of the audio before the first window, and not inside an encoder
  pass: its three graphs (conv, encoder, cross) run through `ggml_backend_sched`, and whisper.cpp installs
  the abort on no backend of it (the sched form of `ggml_graph_compute_helper` sets the thread count
  only). Whisper.net's `WithEncoderBeginHandler` and `WhisperAbortEventHandler` are read at the same
  points. Nothing in the package interrupts an encoder pass.
- **An encoder pass is most of a segment.** At the full context on one thread, 5 s of noise (nothing
  decoded) takes 4.87 s on the laptop, 1.22 s at the sized context (run 44027063937). A stop that lands in a
  pass waits for the rest of it, on one thread or two: two threads took 3.35 s a segment.
- **The 221 to 234 ms on two threads never measured a pass.** Half a second into ten minutes of audio the
  call is still computing the mel spectrogram of all ten minutes, which whisper.cpp splits across its
  threads; one thread doubled that, which is what moved the figure past the bound. The service never hands
  over more than 20 s (`SpeechGateOptions.MaximumSegment`), so its stops land in a pass or a decoder
  step, not there.

`SpeechUnloadTests.AStopInsideAnEncoderPassWaitsForTheRestOfIt` measures both on the laptop, reported, not
enforced. The memory figure in the table above was the stop's release finishing under the memory test:
it now waits, up to 30 s, until no recogniser in the process holds a model before it reads "before", and
the stop test waits for its own release (`WhisperRecogniser.Released`) before it returns.

Laptop run 44139012468 (one thread, full context, base.en):

| What | Figure |
|---|---|
| A 20 s segment: its encoder pass begins | 60 ms into the call |
| The same segment, whole call | 4939 ms |
| Stopped 100 ms into its encoder pass, whisper.cpp lets go | **4677 ms** after the stop (reported) |
| The mel spectrogram of ten minutes, before the first window | 1370 ms |
| Ten minutes in flight, stopped 500 ms in: the disposal returns | **509 ms** (enforced under 650 ms) |
| The same: the native call lets go and the model is freed | **890 ms** after the stop (enforced under 8 s), so left past the bound |
| The idle unload, measured from no model held | 94.1 MB before, +108.9 MB loaded, 152.2 MB given back |

890 ms is the rest of the ten-minute mel (1370 ms less the 500 ms already spent), as predicted. A stop in a
service-sized segment's pass waits nearly the whole pass.

What would let a stop go inside the bound, none of it done here: whisper.cpp installing the abort on its
sched backends (the CPU backend reads one after every graph node), upstream and then in a Whisper.net
release, or in a runtime built for ScreenTrail; the sized context, which shortens the pass about four
times for a short segment (the owner's decision after ST-027's recorded speech); or transcription in a
process of its own that the stop ends. Thread count and context size are the owner's, from ST-031.

### The sized encoder context, off until real speech says otherwise

`AudioContext` (Core) picks the smallest of 512 or 1024 positions that holds the segment plus a second,
and the full 1500 for anything longer than 19.4 s. `WhisperRecogniser` builds one processor per size from
the same model, so the extra states cost no measurable memory: loaded and used on 2.2 s, 10.3 s and
20.7 s, the full recogniser took 149.2 MB private and the sized one (three states) 147.3 MB. It is off by
default; set `SCREENTRAIL_SPEECH_SIZED_CONTEXT=1` (or `true`, `on`) for the service, or for
`ScreenTrail.Service transcribe`, to turn it on. The service logs `encoder window sized to each segment`
or `full` at start.

Word error on synthetic speech, three voices, run 44027063937:

| Clip | Length | Sized context | Word error, full | Word error, sized |
|---|---|---|---|---|
| Short phrase | 2.1 to 2.2 s | 512 | 0% | 0% |
| One sentence | 3.9 to 4.2 s | 512 | 8% | 8% (the same word, both ways) |
| Two sentences | 9.6 to 10.3 s | 1024 | 0% | 0% |
| Three sentences | 14.4 to 15.9 s | 1024 | 0% | 0% |
| All, 270 words | | | 1.1% | 1.1% |

Five seconds of noise invented no words either way. There is no recorded speech in the repository yet
(`research/fixtures/audio/` holds only its README), so word error on a real technician's voice is
unmeasured. The owner decides the default once ST-027's recorded narration exists.

**On real speech it hears more that was not said (2026-11-19, `research/eval/real_speech/RESULTS.md`).**
The same pipeline, run on a Mac in a Linux container over public recordings, both ways:

| Recording | Words said | Word error, full | Word error, sized | Inserted words, full | sized |
|---|---|---|---|---|---|
| A meeting's main speaker, own headset (AMI ES2004a, B) | 1,067 | 14.5% | 14.9% | 19 | 23 |
| The other three, own headsets (A, C, D) | 1,547 | 49.3% | 65.8% | 466 | 712 |
| Read-aloud speech (LibriSpeech test-clean) | 2,017 | 3.8% | 4.5% | 17 | 40 |

One person talking clearly loses little. Quiet stretches and other people's voices, which the full window
calls blank and the hallucination filter drops, come back as words; a sentence is sometimes written
several times; a word is sometimes added after the last one. That is what whisper.cpp is known to do
with less than its thirty-second window. A meeting is not a support call and the laptop did not run this,
so the absolute numbers are not ScreenTrail's; the direction is clear enough that the switch stays off as
built. ST-027's recording is still what its own criterion needs, and a narrated session still trips the
throttle.

### The demo build

`scripts/windows/demo/start.ps1` published Debug (`run-local.ps1`'s default) while CI measures Release;
the pattern search alone ran about 1.6 times slower in Debug, measured on a Mac. It now publishes Release, and
`client-build.txt` says which.

## How the measurements work

- **In the test process, on purpose.** The real service and UI open the technician's store, key, token
  and pipe, which on the laptop are the demo's, so CI does not start them. `ServiceRig` composes the
  service's running parts the way `CaptureHost` does (watchers, coordinator, guards, hook lifetime,
  click drain, scene sampler, redaction worker with real OCR, narration with the real microphone and the
  model never loaded), and the shell test runs the real `LiveShell` against a fake idle service. When
  `CaptureHost` gains a loop, add it to `ServiceRig`.
- **Net of a baseline.** Each test measures the test process alone first, then composes, settles 10 s
  and measures 30 s (60 s for recording) in slices. Every figure is the median slice less the host's
  median slice, which ignores a slice of somebody else's work. Rates divide by the time that actually
  passed, so a late timer never makes a number wrong.
- **CPU from cycles.** Windows' processor time, the one Task Manager shows, charges a whole 15.625 ms
  tick to whichever thread is running when it comes, so an idle five-second slice can only read 0,
  0.31, 0.62 or 0.94%. The tests count the process's cycles, calibrated against a spin, and report the
  tick figure beside it.
- **Wakeups from the kernel.** Context switches per thread, read with `NtQuerySystemInformation`. The
  reader checks every thread record names this process, so a wrong layout fails instead of reporting
  nonsense.
- **No skips, no pictures, no titles.** The tests run on every job. On a hosted runner the windows are
  half a second, nothing is enforced, and the speech test says it did not measure rather than download
  148 MB. The laptop keeps its model copy in `%LOCALAPPDATA%\ScreenTrail.Tests\models`. Measurements are
  text in the job summary and `measurements.txt`; nothing here writes an image or a window title.
- **Cost.** The laptop capabilities job grew from about 2 to 5.4 minutes, and the hosted Windows tests
  by about half a minute. With the breakdown and the narrated sessions (ST-031, 2026-11-17) the
  capabilities job is 24.7 minutes against its 30-minute limit (tests 23.3 minutes, run 44027063937).
  If it nears the limit, the breakdown's voices (three) and session lengths are what to trim first.

## Whole-process numbers, by hand

The CI figures are what ScreenTrail's code adds. The whole process (the runtime, SQLCipher, the OCR
engine, the speech model) is measured on the real processes during a demo rehearsal:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\windows\measure-idle.ps1 -Label idle
powershell -ExecutionPolicy Bypass -File scripts\windows\measure-idle.ps1 -Label "recording, speaking"
```

It reads processor time and memory through `Get-Process` and nothing else, and prints a Markdown table.
Paste the tables here.

## What only the laptop, or a person, can tell us

- Whole-process memory of the service and the UI, and the 600 MB budget with speech: the script above.
- Speech CPU and word error with a real person talking. CI narrates with Windows' synthetic voices, which
  are cleaner than a technician on a headset; ST-027's recorded fixture is still owed.
- ST-031's 30-minute golden replay (ST-030's sessions) and the nightly harness that asserts the budgets.
- Battery: the wakeup counts are the proxy, not a power measurement.
- Whether the throttle ever trips in a real session with speech (the owner step above).

## Not done yet in ST-031

- The sighting of the throttle in the real service (seen in the laptop rig; see above).
- A narrated session inside the budget: the sized encoder context does it in the rig and waits on real
  speech; otherwise the throttle's accounting.
- The nightly harness and the golden replay.
- Live CPU and memory in "What's being captured" (it shows the working set only).
