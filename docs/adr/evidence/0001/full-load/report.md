# ST-001 spike run: 2026-09-11 23:22

- Load: whisper on, UIA on, OCR on; ran 5.0 min
- Machine: 8 logical cores, Microsoft Windows 10.0.26200, OS arch X64, process arch X64
- Audio: default microphone (1 input device(s))
- Input: human

## AC1: added input latency < 5 ms under load, no hook silently removed

**Verdict: PASS for this run, but INCONCLUSIVE for AC1 (needs >= 5 min, >= 300 chars typed, full load)**

| Metric | Value |
|---|---|
| Hook callbacks timed (mouse + keyboard) | 417 |
| Callback time p50 / p95 / p99 / max (ms) | 0.001 / 0.004 / 0.009 / 0.548 |
| Callbacks >= 5 ms | 0 |
| Dispatch delay p50 / p99 / max (ms, ~15.6 ms tick resolution) | 0 / 0 / 16 |
| Silent unhook suspected | no (0 watchdog strikes) |
| LowLevelHooksTimeout | not set (Windows default) |
| Input samples dropped (ring full) | 0 |
| Typed chars / bursts / shortcuts / enters | 39 / 1 / 0 / 0 |
| Clicks / debounced / screenshots | 2 / 0 / 2 |
| Whisper 5 s chunks / p50 / max ms per chunk / dropped | 2573.1 / 10808.2 (59 chunks, 0 dropped, 607 words) |
| UIA polls / p95 / max ms | 1141 / 9.5 / 64.3 (errors: none) |
| Screenshot capture+encode p95 / max ms | 228.1 / 228.1 (0 errors, 0 dropped busy) |
| OCR frames / p50 ms / mean confidence | 2 / 2462.2 / 0.88 (0 skipped busy) |
| Process CPU avg-ish p50 / max (% of all cores) | 49.1 / 62.0 |
| Working set max (MB) | 505 |
| GC gen0 / gen1 / gen2, total pause (ms) | 3 / 2 / 1, 6.6 |

## AC2: RDP focused -> FlaUI reports an opaque subtree

**Verdict: NOT EXERCISED - no RDP (mstsc/msrdc) window was focused**

Focus transitions (process, classes and control types only):

| Time | Process | Window class | Focused class | Control type | Children | Area | Verdict |
|---|---|---|---|---|---|---|---|
| 23:22:10 | WindowsTerminal | CASCADIA_HOSTING_WINDOW_CLASS | TermControl | Text | 1 | 88 % | Accessible (1 children, Text) |

## AC3: overlay with WDA_EXCLUDEFROMCAPTURE is absent from captured frames

**Verdict: PASS**

Frames overlapping the overlay: 1; highest marker-pixel share inside the overlay rectangle: 0.00 % (excluded if < 1 %).

## AC4: 4K frame -> <= 1600 px JPEG under 400 KB with legible 9 pt text

**Verdict: NOT EXERCISED in this run - no 4K-wide frame captured (use the legibility command)**

| Frames | Count | Native (max) | Output (max) | JPEG avg / max (KB) |
|---|---|---|---|---|
| all | 2 | 1920x1080 | 1600x900 | 172 / 225 |
| native long edge >= 3840 | 0 | - | - | - |
