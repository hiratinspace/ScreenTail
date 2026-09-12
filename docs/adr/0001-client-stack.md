# ADR-0001: Client stack for the ScreenTail Windows client

- **Status:** Proposed. This stays open until the ST-001 spike has been run on Windows (see [Evidence](#evidence)).
- **Date:** 2026-09-10
- **Ticket:** ST-001
- **Deciders:** project owner; build agent

## Context

The scope document leaves the client language open ("Python or C#/.NET"). The build guide (§5) already assumes .NET 8, WPF and MVVM. Before ST-002 through ST-004 commit the repository to a stack, we need evidence that .NET can handle the hard parts of the capture service together:

1. Low-level mouse and keyboard hooks that add less than 5 ms of input latency and are never silently removed by Windows, while UI Automation polling, Whisper transcription, screenshots and OCR all run in the same process.
2. UI Automation (FlaUI) that correctly reports a remote-desktop canvas as opaque, so later tickets know they must rely on OCR and voice inside remote sessions.
3. A WPF overlay (the future HUD) hidden from our own screen captures via `WDA_EXCLUDEFROMCAPTURE`, which INV-4 requires to be visible to the user.
4. 4K frames downscaled to at most 1600 px as JPEG, under 400 KB, with 9-pt UI text still legible.

The spike lives in [`/spike`](../../spike/README.md).

## Decision (proposed)

Build the Windows client in **C# on .NET** as two processes: a per-user background capture service, and a WPF UI process. The spike validates these components:

| Concern | Choice used in the spike | Package / API | Notes for the real tickets |
|---|---|---|---|
| Input hooks | `WH_MOUSE_LL` and `WH_KEYBOARD_LL` via P/Invoke on a dedicated high-priority thread. Callbacks don't allocate: they time themselves, categorize the key and write to a single-producer/single-consumer ring buffer. | Win32 | The key code is converted to a category inside the callback and never leaves it (INV-2). A watchdog compares `GetLastInputInfo` with the last tick the hook saw to catch silent removal. |
| UI Automation | Focused element polled every 250 ms on its own thread | FlaUI.UIA3 5.0.0 | Only class names and control types are logged; no names or titles (INV-10). |
| Screenshots | Screen-DC BitBlt with `CAPTUREBLT` | System.Drawing.Common 8.0.31 | ST-025 should compare against Windows.Graphics.Capture, which also honors display affinity. |
| Downscale / JPEG | GDI+ high-quality bicubic, quality 80 | System.Drawing.Common | |
| Speech-to-text | whisper.cpp through Whisper.net, ggml `base` model, 5 s chunks | Whisper.net 1.9.1 + Whisper.net.Runtime 1.9.1 | Ships native binaries for win-x64, win-x86 and win-arm64. |
| Microphone | WaveIn at 16 kHz mono | NAudio.WinMM 2.2.1 | NAudio 3.x targets net9.0 only. |
| OCR | Tesseract, `tessdata_fast` English data | Tesseract 5.2.0 | Native binaries are x64/x86 only and need the VC++ runtime. Windows.Media.Ocr is the alternative with no extra dependencies, worth weighing in ST-041. |
| Overlay | WPF window with `WDA_EXCLUDEFROMCAPTURE` and `WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW` | WPF + user32 | Shows up without stealing focus from the remote window. |
| Tray | WinForms `NotifyIcon` | Windows Desktop | ST-071 chooses the real tray approach (for example H.NotifyIcon.Wpf). |

## Evidence

Filled in from the Windows runs. Raw reports go in `docs/adr/evidence/0001/`.

| AC | Criterion | Result | Evidence |
|---|---|---|---|
| 1 | Hooks + UIA + whisper.net active, 5 min of typing → added input latency < 5 ms; no hook removed by the OS | **Latency PASS on real hardware** (worst callback 0.88 ms idle, 0.55 ms under full load; nothing ≥ 5 ms; no hook removed). Typing volume short of the criterion, so the unattended synthetic run completes it. | [Laptop results](#laptop-results-2026-09-11) |
| 2 | RDP window focused → FlaUI reports an opaque subtree | **NOT EXERCISED** — no RDP target available yet | — |
| 3 | Overlay with `WDA_EXCLUDEFROMCAPTURE` absent from the service's capture | **PASS** on the hosted runner and on the laptop | Hosted: [excluded](evidence/0001/hosted-runner/overlay/overlay-check-excluded.png) 0.00% marker pixels, [control](evidence/0001/hosted-runner/overlay/overlay-check-visible.png) 86.31%. Laptop: [excluded](evidence/0001/overlay/overlay-check-excluded.png) has no pill anywhere on screen, [control](evidence/0001/overlay/overlay-check-visible.png) shows it at 84.97% |
| 4 | 4K frame downscaled to ≤ 1600 px JPEG < 400 KB with legible 9-pt UI text | **Size: PASS. Legibility: FAIL below 200% scaling** (hosted runner and laptop agree, synthetic), resolved by decision: OCR runs on the native frame (finding 2a). | [Hosted-runner results](#hosted-runner-results-2026-09-11) |
| 5 | This ADR records the decision and a Python fallback assessment | Drafted | this file |

Test machine: HP laptop (ITS-SPARES09), Intel Core i7-10510U, 4 cores / 8 threads, 16 GB RAM, Windows 11 Pro build 26200, single 1920×1080 display at 150% scaling, physical hardware. A 2019-era mid-range business laptop, which is roughly what a technician runs.

### Laptop results (2026-09-11)

Two 5-minute runs with a person typing: one with hooks only, one with everything on. Files in [`evidence/0001/`](evidence/0001/).

| Metric | Hooks only | Everything on | Budget |
|---|---|---|---|
| Hook callbacks timed | 612 | 417 | — |
| Callback time p50 / p99 / **max** (ms) | 0.001 / 0.018 / **0.88** | 0.001 / 0.009 / **0.55** | < 5 ms |
| Callbacks ≥ 5 ms | 0 | 0 | 0 |
| Hook silently removed | no | no | no |
| Process CPU p50 / max (% of all cores) | 0.2 / 4.1 | 49 / 62 | < 15% recording (ST-031) |
| Working set max | 44 MB | 505 MB | < 600 MB (ST-031) |
| GC pause, total | 0.6 ms | 6.6 ms | — |

- **The hooks are not the problem.** Managed hook callbacks cost about a microsecond, and the worst single callback across both runs was 0.88 ms, with GC pauses of a few milliseconds spread over five minutes. The fallback plan (a native hook shim) isn't needed.
- **Typing volume was short:** 49 and 39 characters, against the 300 the report asks for before it calls AC1 met. The unattended synthetic-typing run on this same laptop supplies a full five minutes of input.
- **Whisper `base`, transcribing continuously:** 59 chunks of 5 s audio, median 2.6 s and worst 10.8 s per chunk, 607 words. Median real-time factor about 0.5, but the worst chunk ran 2× real time, and the process sat at 49–62% CPU with 505 MB resident. Confirms finding 8 on real hardware, and shows ST-031's RAM budget is nearly spent by speech alone.
- **OCR (Tesseract) took 2.46 s per 1600×900 frame**, mean confidence 0.88. See finding 9.
- **Screenshot capture + downscale + encode: 228 ms and 346 ms** for a 1920×1080 window. See finding 10.
- **UI Automation polling is cheap:** 1141 polls, p95 9.5 ms, worst 64 ms, no errors.
- **AC3 passed, with a measurement caveat.** The excluded capture contains no pill anywhere on screen, which is the real proof, and the control capture shows it at 84.97%. But that run recorded the overlay rectangle as 1440×781 instead of the pill's 450×55 (360×44 at 150% scaling), so the percentage sampled the wrong area. Cause unknown; `overlay-check` now records the window handle and pid and warns when the rectangle isn't pill-sized, so the next run says so out loud.

### Hosted-runner results (2026-09-11)

Source: GitHub `windows-latest`, a 2-core VM running Windows 10.0.26100 (Server 2025) with no microphone; runs [34630922433](https://github.com/hiratinspace/ScreenTail/actions/runs/34630922433) and [34631759322](https://github.com/hiratinspace/ScreenTail/actions/runs/34631759322) (after the BitBlt fix). Files are in [`evidence/0001/hosted-runner/`](evidence/0001/hosted-runner/). It's a VM, so performance numbers are indicative only. The laptop and a person at a keyboard provide the numbers that count.

**AC3 overlay exclusion:** with `WDA_EXCLUDEFROMCAPTURE` set, 0.00% of the overlay rectangle matched the marker colour. In the control run (the same overlay started with `--visible`), 86.31% matched; the rest is the transparent rounded corners and the text. The check can see the overlay when it's there, and it isn't there when excluded.

**AC4 legibility** (synthetic 3840×2160 frame full of 9-pt UI text, downscaled to 1600×900):

| Display scale | 9-pt text height, native → out (px) | JPEG q70 / q80 / q90 (KB) | OCR recall, native | OCR recall, downscaled q80 |
|---|---|---|---|---|
| 100% | 12 → 5 | 200 / 246 / 329 | 89% | **0%** |
| 125% | 15 → 6.3 | 225 / 272 / 360 | 97% | **0%** |
| 150% | 18 → 7.5 | 213 / 257 / 341 | 99% | **6%** |
| 200% | 24 → 10 | 175 / 208 / 273 | 99% | 80% |

- The size limit is comfortably met: every quality level stays under 400 KB.
- The legibility clause fails at every common scaling except 200%. This confirms finding 2, which is now a measurement, not a prediction. OCR has to run on the native frame.

**Smoke run** (1 minute, no input; Whisper on synthetic audio, plus UIA and OCR):

- Whisper `base` on 5 s chunks took 3.9 s median and 5.2 s max per chunk, i.e. roughly real time. Process CPU was **83% median and 97% max across both cores**. That is far over the ST-031 recording budget (< 15% on a 4-core machine). Continuous transcription can't fit the budget; see finding 8.
- UIA polling: 229 polls, p95 5.8 ms, max 106 ms, no errors.
- Working set peaked at 416 MB. GC: one collection per generation, 0.6 ms total pause.
- The hooks installed without error but saw no input: nobody was typing, and hosted runners may have no interactive input desktop. AC1 needs the laptop.

## Findings before the Windows run

These came up while building the spike and hold regardless of how the Windows run turns out.

1. **.NET 8 support ends on 10 November 2026, two months from now.** The ecosystem is already moving: NAudio 3.x targets net9.0 only, and Whisper.net ships net10.0 builds. The spike stays on .NET 8 because Guide §5 names it, and retargeting is a one-line change. **Decided 2026-09-11:** the owner chose **.NET 10 (LTS, supported to November 2028)**. Guide §5 is amended in ST-002, which creates `/client` on .NET 10.
2. **AC4's legibility clause probably fails at common 4K settings.** 9-pt text is 12 px tall at 100% scaling. Downscaling 3840 → 1600 (×0.417) leaves about 5 px, below what either OCR or a person reads reliably. It's about 7.5 px at 150% scaling and 10 px at 200%. The `legibility` command measures this directly. If it fails, the options for ST-025 are:
   - (a) run OCR on the native-resolution frame on the device, and downscale only the copy that is sent or shown;
   - (b) crop around the active window or the click before downscaling;
   - (c) let the long edge scale with DPI, for example 2400 px for 4K at 100%, and re-budget size.
   **Decided 2026-09-11: option (a).** It fits INV-1 and INV-7 best and costs nothing extra over the wire. See Consequences for what it changes in ST-025 and ST-041.
3. **Layered windows can mislead the capture test.** A screen BitBlt without `CAPTUREBLT` can leave out layered windows, so an exclusion test could pass for the wrong reason. The spike uses `CAPTUREBLT` and adds a control run with exclusion switched off, which must detect the overlay. `Graphics.CopyFromScreen` rejects `SourceCopy | CaptureBlt` with an `InvalidEnumArgumentException`; the first hosted run caught this. The spike now calls GDI `BitBlt` directly, and ST-025 must do the same or use Windows.Graphics.Capture.
4. **How latency is measured.** The time spent inside the hook callback, measured with the high-resolution performance counter, is the latency our hook adds while its thread is responsive. The delay between the event and the callback is only accurate to the ~15.6 ms system tick, so it serves as a starvation indicator. The unhook watchdog covers the case where the hook stops responding altogether.
5. **Managed code in hook callbacks.** The main risk is a garbage-collection pause landing inside a callback. Mitigations in place: callbacks that don't allocate, a dedicated thread, and `GCLatencyMode.SustainedLowLatency`. The AC1 verdict uses the *maximum*, not p99, so any such pause shows up. If AC1 fails because of GC pauses, the fallback is a small native hook DLL writing to a shared-memory ring, with everything else staying in .NET. That's a contained change, not a reason to switch languages.
6. **Elevated windows.** Low-level hooks installed by a medium-integrity process don't receive input sent to elevated windows or to the secure desktop. That affects the unhook watchdog (false positives) and is the mechanism behind the "Elevated window, screen not captured" state in ST-021.
7. **Windows on ARM.** Whisper.net has arm64 native binaries; Tesseract doesn't, so the capture process runs as x64 under emulation. If technicians on ARM laptops matter, Windows.Media.Ocr removes this constraint.
8. **Whisper must be gated by voice activity detection, not run continuously.** On the hosted runner, feeding Whisper `base` every 5 s of audio used most of the CPU (see the hosted-runner results). ST-027 should run VAD first (Whisper.net ships a Silero VAD model) and transcribe only speech segments, with CPU rate-limited so capture and hooks always win (ST-031). Technicians talk for a fraction of a session, so this is the difference between being always busy and mostly idle. The laptop run gives the real per-core cost: 49–62% of an 8-thread laptop CPU, and 505 MB resident.
9. **OCR is far slower than ST-041 budgets for.** On the laptop, Tesseract took **2.46 s per 1600×900 frame**, against ST-041's target of a 700 ms median. Finding 2a makes it worse, since OCR will run on the native frame, which is larger. ST-041 needs one of: OCR only the regions that matter (the active window or the area around the click), several worker threads, or Windows.Media.Ocr, which is hardware-accelerated and needs no data files. Measure all three before committing.
10. **Screen capture and encoding cost more than ST-025 budgets for.** Capturing, downscaling and JPEG-encoding one 1920×1080 window took **228 ms and 346 ms** on the laptop, against ST-025's budget of 120 ms for a 4K frame. The path measured here is GDI BitBlt plus GDI+ bicubic downscale and encode, all on the CPU. ST-025 should compare Windows.Graphics.Capture (which keeps the frame on the GPU and honours display affinity) and a WIC encoder before accepting the budget.

## Python fallback assessment

| Area | .NET (proposed) | Python (fallback) | Risk for Python |
|---|---|---|---|
| Low-level hooks | P/Invoke on a dedicated thread; callbacks don't allocate | pynput/ctypes. Callbacks need the GIL, so any thread holding it (the Python side of STT or OCR, image work) delays every keystroke and mouse move system-wide. Avoiding that needs a separate hook process. | **High.** This is exactly what AC1 tests. |
| UI Automation | FlaUI (UIA3, COM) | pywinauto / uiautomation over comtypes. Works, but slower and with less type safety. | Medium |
| Screenshots | GDI or Windows.Graphics.Capture | mss (BitBlt), which is fast and also honors display affinity | Low |
| Speech-to-text | Whisper.net (whisper.cpp) | faster-whisper (CTranslate2, int8), often faster than whisper.cpp on CPU | **Advantage: Python** |
| OCR | Tesseract or Windows.Media.Ocr | PaddleOCR: better on UI text, but a much larger install | Mixed |
| UI process | WPF, with native UIA automation names (Spec §7) and first-class MVVM | PySide6/Qt. Accessibility exists but takes more work to get right. | Medium |
| Packaging and signing | Single-file or MSIX, signs cleanly | PyInstaller bundles often trigger antivirus false positives, which conflicts with ST-112's "no SmartScreen/AV warning" | **High** |
| Team fit | Guide §5, backend (ASP.NET Core) and tests (xUnit) are all .NET | `/research` is already Python and stays that way | — |

**Conclusion:** Python stays the language for `/research` (prompts, eval harness), not for the client. If faster-whisper's speed advantage turns out to matter, ST-027 can benchmark it against Whisper.net at the model level. If AC1 fails, the answer is a native hook shim, not a move to Python.

## Consequences

- ST-002 scaffolds `/client` as a .NET 10 solution: a capture service, a WPF UI, a shared contracts project and tests.
- **ST-025 / ST-041 (finding 2a):** each click frame is kept at native resolution, encrypted and marked `redaction_pending`, until the redaction worker has run OCR and masking on it. Only then is the downscaled copy (≤ 1600 px) written for Review and the bundle builder, and the native frame deleted. INV-1 is unchanged: nothing is readable before redaction. ST-005's disk budget (< 40 MB per 20-minute session) applies to what remains after redaction; the native frames are transient.
- **ST-027 (finding 8):** transcription is gated by voice activity detection.
- Platform-neutral logic lives in projects that target plain `netX.0` so it can be unit-tested on any OS. Windows-only code sits behind thin adapters.
- `/spike` is deleted once this ADR is accepted.

## To accept this ADR

1. Run the steps in `spike/README.md` on a Windows x64 machine.
2. Fill in the Evidence table and the test machine line, and attach the reports under `docs/adr/evidence/0001/`.
3. Resolve finding 1 (the .NET version) and, if AC4 fails, choose among the options in finding 2.
4. Change Status to **Accepted**, or record the amendment.
