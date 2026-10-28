# Performance budgets (ST-031)

ST-031 sets ScreenTail's budgets: idle CPU under 1%, recording CPU under 15% of a four-core machine,
memory under 600 MB with speech, and disk under 40 MB for a twenty-minute session. This page says how
each one is measured, whether CI enforces it, and the latest numbers from the test laptop (SCREENTRAIL,
Windows 11, 8 logical processors).

## The budgets

| Budget | Measured by | Enforced | Latest on the laptop (2026-10-27) |
|---|---|---|---|
| Idle CPU, service, under 1% | `ServiceCostTests.WithNoSessionTheServiceIsAlmostSilent` | Yes, on the laptop, against 1% of **one core** | 0.32% of one core net (0.04% of the machine) |
| Idle CPU, UI, under 1% | `ShellCostTests.WithNoSessionTheShellIsAlmostSilent` | Yes, on the laptop, against 1% of one core | 0.0% net (0.22% of one core gross, the test host included) |
| Idle wakeups, service | the same test | Yes, on the laptop, a ceiling of 40 a second (set from the laptop, not named by ST-031) | 14 to 17 a second net, down from 129 |
| Idle wakeups, UI | the shell test | Reported only | 1.4 a second net (the pill reports every 2 s) |
| Recording CPU under 15% of four cores (60% of one core) | `ServiceCostTests.ARecordingSessionStaysInsideItsProcessorAndDiskBudgets` | Yes, on the laptop | 4.4% and 4.9% of one core in two runs, without speech |
| Disk under 40 MB per 20 minutes | the same test | Yes, on the laptop, for the 85-frame session `SessionStoreTests` assumes | 76 KB a stored frame, 6.3 MB for 85 frames; 1.4 MB per session-minute at one text frame every 6 s |
| Per-key and per-click hook cost under 1 ms | `InputHookTests` (laptop input job) | Yes, since ST-024 | Worst callback 107 µs typing 860 keys, 3.6 µs per click |
| Memory after the speech model's idle unload | `SpeechUnloadTests` | Reported only (first measurement) | base.en adds 147 MB loaded; the unload gives back 156 MB |
| Memory under 600 MB with speech, whole process | `scripts/windows/measure-idle.ps1`, run by hand | No | Not run yet; ADR-0001 measured 505 MB with continuous transcription |

Idle CPU is held against one core rather than the machine, which is stricter than the budget: 1% of
the laptop's eight processors is 8% of one core, which nothing at idle would ever reach, so it could
not catch a regression that matters.

Wakeups: two laptop runs of the same code measured 17 and 14 a second net, with slices from 15 to 21,
so a ceiling of 40 is enforced. It leaves room for that spread and still fails a ten-a-second poll
coming back. The UI's figure is within the host's own noise (one to five a second either way), so it is
reported, not enforced.

## What was fixed

A code read found the service waking about forty times a second with no session, each time to find
there was nothing to do. The first laptop run measured it at **129 wakeups a second and 2.5% of one
core** (median slice, clock ticks), over the 1% budget, and that run failed the job as it should.

| Loop | Before, with no session | Now |
|---|---|---|
| Click drain (`ClickCaptureLoop`) | 20 a second, empty buffer | A timer only while a session is under way |
| Redaction workers (`RedactionWorker`) | 2 workers, 5 a second each | Wait for a frame to arrive; back off only after a failure |
| Sensitive-context guard | 4 a second | Only while a session is under way; acts at the session's first moment |
| Scene sampler | 1 a second | Only while a session is under way |
| Password-field guard | 1 a second backstop | Backstop only during a session or a hold; focus changes still wake it |
| Narration recorder | 2 a second backstop | Every 5 s; the nudge on each state change is the mechanism |

`SessionLoop` (Core) is the shared rule: a timer exists only in the states the input hooks are in
(recording, paused, suppressed, finalizing), the step runs at once when a session begins and once more
after it ends. Nothing on the hook path changed.

| Laptop run | Service idle wakeups (net) | Service idle CPU, median slice |
|---|---|---|
| 40585899774, before | 129 a second | 2.5% of one core by clock ticks (failed) |
| 40587029859, loops fixed | 25 a second | 0.94% by clock ticks, which is three ticks |
| 40588642451, narration too | 17 a second | 0.32% net by cycles (0.94% by clock ticks) |
| 40623029883, same code again | 14 a second | 0.36% net by cycles (0.62% by clock ticks) |

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

## Not done yet in ST-031

- Throttling sampling and OCR first when CPU has been over budget for 10 s.
- The nightly harness and the golden replay.
- Live CPU and memory in "What's being captured" (it shows the working set only).
