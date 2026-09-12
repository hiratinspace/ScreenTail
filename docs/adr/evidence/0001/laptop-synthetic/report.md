# ST-001 spike run: 2026-09-12 00:12

- Load: whisper on, UIA on, OCR on; ran 5.0 min
- Machine: 8 logical cores, Microsoft Windows 10.0.26200, OS arch X64, process arch X64
- Audio: default microphone (1 input device(s))
- Input: synthetic (SendInput into the typing target)
- Synthetic keys / clicks / skipped (target not foreground): 2368 / 51 / 0

## AC1: added input latency < 5 ms under load, no hook silently removed

**Verdict: PASS (synthetic input)**

| Metric | Value |
|---|---|
| Hook callbacks timed (mouse + keyboard) | 4986 |
| Callback time p50 / p95 / p99 / max (ms) | 0.001 / 0.002 / 0.008 / 0.445 |
| Callbacks >= 5 ms | 0 |
| Dispatch delay p50 / p99 / max (ms, ~15.6 ms tick resolution) | 0 / 0 / 16 |
| Silent unhook suspected | no (0 watchdog strikes) |
| LowLevelHooksTimeout | not set (Windows default) |
| Input samples dropped (ring full) | 0 |
| Typed chars / bursts / shortcuts / enters | 2337 / 32 / 0 / 31 |
| Clicks / debounced / screenshots | 51 / 0 / 51 |
| Whisper 5 s chunks / p50 / max ms per chunk / dropped | 2688.3 / 6253.5 (59 chunks, 0 dropped, 527 words) |
| UIA polls / p95 / max ms | 1118 / 21.0 / 55.5 (errors: none) |
| Screenshot capture+encode p95 / max ms | 178.5 / 199.5 (0 errors, 0 dropped busy) |
| OCR frames / p50 ms / mean confidence | 51 / 1186.6 / 0.92 (0 skipped busy) |
| Process CPU avg-ish p50 / max (% of all cores) | 41.3 / 65.8 |
| Working set max (MB) | 529 |
| GC gen0 / gen1 / gen2, total pause (ms) | 5 / 2 / 1, 6.9 |

## AC2: RDP focused -> FlaUI reports an opaque subtree

**Verdict: NOT EXERCISED - no RDP (mstsc/msrdc) window was focused**

Focus transitions (process, classes and control types only):

| Time | Process | Window class | Focused class | Control type | Children | Area | Verdict |
|---|---|---|---|---|---|---|---|
| 00:12:55 | WindowsTerminal | CASCADIA_HOSTING_WINDOW_CLASS | TermControl | Text | 1 | 88 % | Accessible (1 children, Text) |
| 00:12:56 | Spike.Overlay | Window | TextBox | Edit | 0 | 95 % | Accessible (0 children, Edit) |

## AC3: overlay with WDA_EXCLUDEFROMCAPTURE is absent from captured frames

**Verdict: PASS**

Frames overlapping the overlay: 51; highest marker-pixel share inside the overlay rectangle: 0.00 % (excluded if < 1 %).

## AC4: 4K frame -> <= 1600 px JPEG under 400 KB with legible 9 pt text

**Verdict: NOT EXERCISED in this run - no 4K-wide frame captured (use the legibility command)**

| Frames | Count | Native (max) | Output (max) | JPEG avg / max (KB) |
|---|---|---|---|---|
| all | 51 | 1920x1080 | 1600x900 | 105 / 173 |
| native long edge >= 3840 | 0 | - | - | - |
