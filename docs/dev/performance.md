# Performance budgets (ST-031)

ST-031 sets ScreenTail's budgets: idle CPU under 1%, recording CPU under 15% of a four-core machine,
memory under 600 MB with speech, and disk under 40 MB for a twenty-minute session. This page says how
each one is measured, whether CI enforces it, and the latest numbers from the test laptop (SCREENTRAIL,
Windows 11, 8 logical processors).

## The budgets

The latest figures are from laptop run 40626688381 (2026-10-28, commit 9f4113d, the branch's last code
change).

| Budget | Measured by | Enforced | Latest on the laptop |
|---|---|---|---|
| Idle CPU, service, under 1% | `ServiceCostTests.WithNoSessionTheServiceIsAlmostSilent` | Yes, on the laptop, against 1% of **one core** | 0.41% of one core net (0.05% of the machine) |
| Idle CPU, UI, under 1% | `ShellCostTests.WithNoSessionTheShellIsAlmostSilent` | Yes, on the laptop, against 1% of one core | 0.0% net (0.22% of one core gross, the test host included) |
| Idle wakeups, service | the same test | Yes, on the laptop, a ceiling of 40 a second (set from the laptop, not named by ST-031) | 17 a second net (14 to 17 across four runs), down from 129 |
| Idle wakeups, service, on a busy desktop | `ServiceCostTests.WithNoSessionABusyDesktopDoesNotWakeTheService`, `IdleHookTests` | Each watcher's pump thread under 10 a second, everywhere (hosted runner included); the whole service under the same ceiling of 40, on the laptop | Hosted: 0.0 a second for each pump with 68 renames a second behind the window in front; the laptop has not run it yet |
| Idle wakeups, UI | the shell test | Reported only | 1.4 a second net (the pill reports every 2 s) |
| Recording CPU under 15% of four cores (60% of one core) | `ServiceCostTests.ARecordingSessionStaysInsideItsProcessorAndDiskBudgets` | Yes, on the laptop | 4.7% of one core without speech (4.4 to 4.9% across four runs); conservative, since the test's own repainting and staging are counted with it |
| Disk under 40 MB per 20 minutes | the same test | Yes, on the laptop: the measured frame size times the 85-frame session `SessionStoreTests` assumes | 70 KB a stored frame, 5.8 MB for 85 frames. At the test's own rate of one text frame every 6 s it is 1.5 MB per session-minute, about 30 MB per 20 minutes; that figure is reported, not enforced |
| Per-key and per-click hook cost under 1 ms | `InputHookTests` (laptop input job) | Yes, since ST-024 | Worst callback 145 µs typing 860 keys, 0.9 µs per click |
| Memory after the speech model's idle unload | `SpeechUnloadTests` | Reported only (first measurement) | base.en adds 150 MB loaded; the unload gives back 158 MB |
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
  falls back to every thread, the old subscription, and never to the poll alone.
- The focus hook is **in only while a session is under way or a password-field hold is kept**
  (`FocusInterest`, Core, and `WindowsFocusWatcher.Listen`). At a session's start the guard asks what has
  focus on the transition itself, as before, and its one-second backstop covers a focus move in the
  moment before the hook is back.
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

The laptop runner was offline for the rest of that day (`SCREENTRAIL`, offline), so the laptop has not yet
confirmed which thread was the busy one. The idle reports now name the busiest threads by their own
description (`.NET TP Worker`, `ScreenTail foreground watcher`, never a window title) and give each
watcher pump's rate, so the next laptop run says whose the wakeups are.

## Over budget: what gives first (AC2)

Built 2026-10-29 (`client/ScreenTail.Core/Perf`). While a session is under way the service reads its own
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
hide the regression that throttled it. At 4.7% of one core it should never move.

### Owner step on the laptop

Not yet seen on real hardware. During a demo rehearsal, with the service's console log in view:

1. Record a five-minute session while narrating throughout. Expect no `Capture throttle` line. If one
   appears, note the level and percentage it gives and paste them here.
2. Run `scripts\windows\measure-idle.ps1 -Label "recording, speaking"` alongside, so the throttle's
   reading can be compared with what `Get-Process` sees.
3. If the narrated session trips the throttle, check how many scene frames it kept. The count is in
   the service's log at debug level (start the service with `SCREENTAIL_LOG_LEVEL=Debug`; each kept frame
   logs `Scene frame N of M samples`). Compare it with a session of the same length without narration.
   A count near zero means narration alone holds the service over budget and costs the session its scene
   frames; paste both counts here, since that is a reason to revisit the budget or the order in which
   things give.

A session that never trips the throttle is the expected result, since recording measures far under the
budget. Forcing a trip on real hardware would need a deliberate overload inside the service and is not
part of this step.

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
  148 MB. The laptop keeps its model copy in `%LOCALAPPDATA%\ScreenTail.Tests\models`. Measurements are
  text in the job summary and `measurements.txt`; nothing here writes an image or a window title.
- **Cost.** The laptop capabilities job grew from about 2 to 5.4 minutes, and the hosted Windows tests
  by about half a minute.

## Whole-process numbers, by hand

The CI figures are what ScreenTail's code adds. The whole process (the runtime, SQLCipher, the OCR
engine, the speech model) is measured on the real processes during a demo rehearsal:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\windows\measure-idle.ps1 -Label idle
powershell -ExecutionPolicy Bypass -File scripts\windows\measure-idle.ps1 -Label "recording, speaking"
```

It reads processor time and memory through `Get-Process` and nothing else, and prints a Markdown table.
Paste the tables here.

## What only the laptop, or a person, can tell us

- Whole-process memory of the service and the UI, and the 600 MB budget with speech: the script above.
- Speech CPU while somebody talks. CI has nobody talking, so the recording figure has no transcription.
- ST-031's 30-minute golden replay (ST-030's sessions) and the nightly harness that asserts the budgets.
- Battery: the wakeup counts are the proxy, not a power measurement.
- Whether the throttle ever trips in a real session with speech (the owner step above).

## Not done yet in ST-031

- The laptop sighting of the throttle (built and proved on the Mac suite; see above).
- The nightly harness and the golden replay.
- Live CPU and memory in "What's being captured" (it shows the working set only).
