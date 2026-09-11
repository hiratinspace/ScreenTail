# ST-001 spike run: 2026-09-11 18:13

- Load: whisper on, UIA on, OCR on; ran 1.0 min
- Machine: 2 logical cores, Microsoft Windows 10.0.26100, OS arch X64, process arch X64
- Audio: no microphone; synthetic audio
- Input: human

## AC1: added input latency < 5 ms under load, no hook silently removed

**Verdict: INCONCLUSIVE - no hook callbacks recorded**

| Metric | Value |
|---|---|
| Hook callbacks timed (mouse + keyboard) | 0 |
| Callback time p50 / p95 / p99 / max (ms) | 0.000 / 0.000 / 0.000 / 0.000 |
| Callbacks >= 5 ms | 0 |
| Dispatch delay p50 / p99 / max (ms, ~15.6 ms tick resolution) | 0 / 0 / 0 |
| Silent unhook suspected | no (0 watchdog strikes) |
| LowLevelHooksTimeout | not set (Windows default) |
| Input samples dropped (ring full) | 0 |
| Typed chars / bursts / shortcuts / enters | 0 / 0 / 0 / 0 |
| Clicks / debounced / screenshots | 0 / 0 / 0 |
| Whisper 5 s chunks / p50 / max ms per chunk / dropped | 3798.7 / 3888.3 (11 chunks, 0 dropped, 22 words) |
| UIA polls / p95 / max ms | 230 / 5.7 / 219.6 (errors: none) |
| Screenshot capture+encode p95 / max ms | - / - (0 errors, 0 dropped busy) |
| OCR frames / p50 ms / mean confidence | 0 / - / 0.00 (0 skipped busy) |
| Process CPU avg-ish p50 / max (% of all cores) | 85.4 / 96.4 |
| Working set max (MB) | 412 |
| GC gen0 / gen1 / gen2, total pause (ms) | 1 / 1 / 1, 0.6 |

## AC2: RDP focused -> FlaUI reports an opaque subtree

**Verdict: NOT EXERCISED - no RDP (mstsc/msrdc) window was focused**

Focus transitions (process, classes and control types only):

| Time | Process | Window class | Focused class | Control type | Children | Area | Verdict |
|---|---|---|---|---|---|---|---|
| 18:13:49 | WindowsTerminal | CASCADIA_HOSTING_WINDOW_CLASS | TermControl | Text | 1 | 91 % | Accessible (1 children, Text) |

## AC3: overlay with WDA_EXCLUDEFROMCAPTURE is absent from captured frames

**Verdict: NOT EXERCISED in this run - no captured window overlapped the overlay (use overlay-check)**

Frames overlapping the overlay: 0; highest marker-pixel share inside the overlay rectangle: 0.00 % (excluded if < 1 %).

## AC4: 4K frame -> <= 1600 px JPEG under 400 KB with legible 9 pt text

**Verdict: NOT EXERCISED in this run - no 4K-wide frame captured (use the legibility command)**

| Frames | Count | Native (max) | Output (max) | JPEG avg / max (KB) |
|---|---|---|---|---|
| all | 0 | - | - | - |
| native long edge >= 3840 | 0 | - | - | - |
