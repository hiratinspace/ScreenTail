# ST-001 spike run: 2026-09-11 23:16

- Load: whisper off, UIA off, OCR off; ran 5.0 min
- Machine: 8 logical cores, Microsoft Windows 10.0.26200, OS arch X64, process arch X64
- Audio: not started
- Input: human

## AC1: added input latency < 5 ms under load, no hook silently removed

**Verdict: PASS for this run, but INCONCLUSIVE for AC1 (needs >= 5 min, >= 300 chars typed, full load)**

| Metric | Value |
|---|---|
| Hook callbacks timed (mouse + keyboard) | 612 |
| Callback time p50 / p95 / p99 / max (ms) | 0.001 / 0.005 / 0.018 / 0.884 |
| Callbacks >= 5 ms | 0 |
| Dispatch delay p50 / p99 / max (ms, ~15.6 ms tick resolution) | 0 / 0 / 16 |
| Silent unhook suspected | no (0 watchdog strikes) |
| LowLevelHooksTimeout | not set (Windows default) |
| Input samples dropped (ring full) | 0 |
| Typed chars / bursts / shortcuts / enters | 49 / 3 / 0 / 0 |
| Clicks / debounced / screenshots | 5 / 1 / 4 |
| Whisper 5 s chunks / p50 / max ms per chunk / dropped | - / - (0 chunks, 0 dropped, 0 words) |
| UIA polls / p95 / max ms | 0 / - / - (errors: none) |
| Screenshot capture+encode p95 / max ms | 345.5 / 345.5 (0 errors, 0 dropped busy) |
| OCR frames / p50 ms / mean confidence | 0 / - / 0.00 (0 skipped busy) |
| Process CPU avg-ish p50 / max (% of all cores) | 0.2 / 4.1 |
| Working set max (MB) | 44 |
| GC gen0 / gen1 / gen2, total pause (ms) | 1 / 1 / 1, 0.6 |

## AC2: RDP focused -> FlaUI reports an opaque subtree

**Verdict: NOT EXERCISED - no RDP (mstsc/msrdc) window was focused**

Focus transitions (process, classes and control types only):

| Time | Process | Window class | Focused class | Control type | Children | Area | Verdict |
|---|---|---|---|---|---|---|---|

## AC3: overlay with WDA_EXCLUDEFROMCAPTURE is absent from captured frames

**Verdict: PASS**

Frames overlapping the overlay: 2; highest marker-pixel share inside the overlay rectangle: 0.00 % (excluded if < 1 %).

## AC4: 4K frame -> <= 1600 px JPEG under 400 KB with legible 9 pt text

**Verdict: NOT EXERCISED in this run - no 4K-wide frame captured (use the legibility command)**

| Frames | Count | Native (max) | Output (max) | JPEG avg / max (KB) |
|---|---|---|---|---|
| all | 4 | 1920x1080 | 1600x900 | 79 / 157 |
| native long edge >= 3840 | 0 | - | - | - |
