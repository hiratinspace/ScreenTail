# ST-001 spike: client stack validation (disposable)

This is throwaway code that answers one question: can a .NET background process run low-level hooks, UI Automation, screenshots, OCR and Whisper at the same time without adding noticeable input lag, while a separate WPF process shows a tray icon and an overlay hidden from capture? The results go into [`/docs/adr/0001-client-stack.md`](../docs/adr/0001-client-stack.md). Nothing here ships. `/client` starts fresh in ST-002.

| Project | Runs on | What it is |
|---|---|---|
| `Spike.Core` | anywhere | Logic with no platform dependency (latency stats, key categories, ring buffer, downscale math, opaque-canvas check, unhook watchdog, overlay pixel probe, OCR recall). |
| `Spike.Core.Tests` | anywhere | 70 xUnit tests. `dotnet test Spike.Core.Tests` |
| `Spike.Capture` | Windows x64 | Console app standing in for the capture service. |
| `Spike.Overlay` | Windows | WPF app standing in for the UI process: a tray icon plus a magenta overlay pill hidden from capture. |

Privacy: the spike records no key identities (only the categories `typing_burst`, `shortcut` and `enter` with counts), no window titles, no OCR text and no transcript text. It does save up to 25 JPEG frames of your screen into the run folder, so look through them before you share that folder.

## Prerequisites (Windows machine)

- Windows 11, or Windows 10 version 2004 or later (needed for `WDA_EXCLUDEFROMCAPTURE`). Use an x64 PC if you can. On Windows on ARM the capture app runs under x64 emulation, which makes the latency numbers unrepresentative. If that's all you have, say so when you send results.
- .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10` (the spike began on .NET 8; ADR-0001 finding 1 moved the project to 10)
- Visual C++ 2015–2022 x64 runtime, needed by Tesseract: `winget install Microsoft.VCRedist.2015+.x64`
- A microphone. It's optional: without one, synthetic audio keeps the speech-to-text CPU load the same.
- An RDP target (any Windows PC or VM with Remote Desktop enabled) for AC2.
- A 4K monitor if you have one, for AC4. Without one, the synthetic 4K test still covers it.
- Internet access on first run. It downloads the Whisper base model (about 150 MB) and Tesseract English data (about 4 MB) into `%LOCALAPPDATA%\ScreenTail.Spike`.

## Build

```powershell
cd spike
dotnet build -c Release
```

## Automated checks (GitHub Actions)

On every pull request that touches `spike/`, `.github/workflows/spike-windows.yml` runs `ci-checks.ps1` in two jobs. See [`docs/dev/windows-test-loop.md`](../docs/dev/windows-test-loop.md) for how they fit together.

- **Hosted Windows runner:** build, unit tests, the overlay check with its control case (AC3), synthetic legibility (AC4), and a 1-minute smoke run that makes sure the hooks, UIA, Whisper and OCR all start. It's a VM, so its numbers don't count; it catches crashes before anyone spends time at the keyboard.
- **Spare laptop (self-hosted, once enabled):** the same checks on real hardware, plus a 5-minute full-load run where `--drive-input` types and clicks into the overlay's `--typing-target` window. That gives AC1 numbers for that laptop with nobody present.

Results appear on each run's summary page and in the `st-001-evidence-*` artifacts.

## Easiest path: the guided script (about 25 minutes)

From `spike\` on the Windows machine, with the `st-001-client-stack-spike` branch checked out:

```powershell
powershell -ExecutionPolicy Bypass -File .\run-spike.ps1 -Push
```

The script pulls and builds, records machine info, runs the overlay check by itself, prompts you through the two 5-minute runs, runs legibility, then deletes the saved frames and prints the verdicts. With `-Push` it asks you to confirm and then commits `docs\adr\evidence\0001` to the branch and pushes it. Options: `-IncludeFrames` keeps the screenshots; `-SkipBaseline` skips the hooks-only run.

## Manual runs

The same steps by hand. Each run prints its output folder (`%LOCALAPPDATA%\ScreenTail.Spike\runs\<timestamp>-<command>`). `Ctrl+C` ends a run early and still writes the report. In the commands below, `cap` stands for `dotnet run -c Release --project Spike.Capture --`.

1. **Start the overlay.** In a separate terminal, run `dotnet run -c Release --project Spike.Overlay`. A magenta pill appears top-right and an info icon appears in the tray.
2. **Overlay exclusion (AC3) with a control case:**
   - `cap overlay-check --label excluded` should print `EXCLUDED`.
   - Right-click the tray icon and **uncheck** "Exclude overlay from capture" (or start the overlay with `--visible`). Then run `cap overlay-check --label visible`, which should print `CAPTURED`. This proves the check can actually see the overlay. Re-check the menu item afterwards.
3. **Baseline latency, hooks only:** `cap run --minutes 5 --no-whisper --no-uia --no-ocr`. Type continuously in Notepad for the full 5 minutes and click now and then.
4. **Full load (AC1, AC2 and AC3 in one run):** `cap run --minutes 5`. Leave the overlay running.
   - For the first ~3 minutes, maximize Notepad and type and click continuously. Talk while you work so the mic has speech.
   - For the last ~2 minutes, open `mstsc` to your RDP target, maximize it, and click and type inside the remote session.
5. **Legibility (AC4):** `cap legibility`. If your primary monitor is 4K, add `--from-screen` and have something text-heavy on screen first (for example, the Services console).

## What to send back

`run-spike.ps1 -Push` handles this. If you ran the steps by hand, copy the run folders into `docs/adr/evidence/0001/` on the branch and push (leave out `frames/` unless you're comfortable sharing it), or paste each `report.md`, `overlay-check-*.txt` and `legibility.md`. Also note:

- CPU model and core count, and RAM
- Display resolution and scaling
- Windows edition and build
- Whether it's physical hardware or a VM, and x64 or ARM

## Reading the results

- **AC1:** `Callback time max` must stay under 5 ms and `Silent unhook suspected` must be `no`. Dispatch delay is quantized to the ~15.6 ms system tick, so it only shows whether the hook thread got starved; it isn't a precise latency. Compare the baseline run with the full-load run.
- **AC2:** the focus-transition table should show `mstsc` with `Opaque`. The class names it records tell ST-022/ST-023 what to match on.
- **AC3:** `EXCLUDED` in the excluded run and `CAPTURED` in the control run. The full-load run also probes every frame that overlaps the overlay.
- **AC4:** JPEG KB for 3840-wide frames, plus the OCR recall and the saved JPEGs for judging legibility by eye.
