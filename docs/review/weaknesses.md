# ScreenTail — ranked weaknesses

**Date:** 2026-09-15 · **Against:** `main` @ 35/81 tickets · **Method:** four independent adversarial
reviews (security, invariant enforcement, performance, test quality), every headline claim re-verified by
hand against the code before being written down here.

**Status, 2026-09-15:** **ST-048 fixes P0-1, P0-3, P0-4, P0-5 and P2-1.** Each of those sections now opens
with what was done. P0-2 and P1-1, P1-2 and P1-6 are ST-085 (wire the UI to the service); P1-4 is ST-018;
the rest of P1 and the scheduled P2s are ST-049. Nothing here is closed by being ticketed — only the five
marked **Fixed** are.

## How this is ranked

By **what the product cannot survive being wrong about**, not by how hard it is to fix.

ScreenTail's premise is that a technician can point a recording tool at a customer's screen because the
recording is redacted, visibly indicated, and never published unreviewed. Anything that breaks one of
those three is ranked above anything that merely costs CPU, however expensive.

| Band | Meaning |
|---|---|
| **P0** | Do not run against a real customer session until fixed. |
| **P1** | Fix before the ST-110 pilot. |
| **P2** | Real cost or real risk; schedule deliberately. |
| **P3** | Correct when convenient. |

A recurring theme runs through P0 and P1 and is worth naming once: **the enforcement points are weaker
than the policy objects.** The decision classes in `ScreenTail.Core` are well written and well tested. The
places that *apply* those decisions — the Windows glue, the loops, the UI process — are frequently
untested, unwired, or both. Every P0 below is an enforcement gap, not a policy error.

---

# P0 — ship-blockers

## P0-1 · An unredacted screenshot can be stored as redacted (INV-1)

> **Fixed in ST-048.** Zero words is now treated as unreadable and the frame is discarded; the two discard
> reasons are counted apart as `Unread` and `Unreadable`. Three tests in `RedactionWorkerTests` cover it,
> and all three fail if the check is removed. The design question this raised — whether a genuinely blank
> frame should be kept — is decided and argued in **ADR-0004**: it is not, and the cost is accepted.

**`client/ScreenTail.Core/Privacy/RedactionWorker.cs:235`**

```csharp
if (!text.IsEmpty && text.MeanConfidence < _options.MinimumConfidence)
```

When OCR returns **zero words**, `!text.IsEmpty` is false, so the discard is skipped. `RedactFrame([])`
then yields no regions, `Complete` is true, `WindowsFrameMasker.Paint` early-returns at
`WindowsFrameMasker.cs:58`, and the **native screenshot is re-encoded unchanged** and written with
`redaction_pending = 0` (`SqliteSessionStore.cs:186-208`). It is now visible to Review, export and publish.

The comment two lines above says *"A frame the recogniser could not make out is where a secret is most
likely to survive, so it goes rather than being stored unchecked."* The guard exempts precisely that case.

**Compounding:** `client/ScreenTail.Service/Privacy/WindowsOcrRecogniser.cs:32,83` always returns
`UnknownConfidence = 1.0`, so the confidence gate beneath it can never fire on the only platform that
ships. Two independent defences, both inert.

**Scenario.** Windows OCR returns nothing for a dark-mode terminal, an unusual DPI, a CJK or Cyrillic page
with no language pack (`TryCreateFromUserProfileLanguages()` falls back to `en-US`), or a frame it decoded
but could not segment. The customer's screen is stored verbatim and marked clean. Nothing counts it —
"nothing on screen" and "read nothing" both land in the success counter.

**Fix.** Treat empty OCR as unreadable: drop the `!text.IsEmpty &&`. Count the two cases separately so the
difference is visible. Then decide whether a frame with genuinely no text (a blank desktop) should be kept
— that is the open design question from ST-041 and it now has teeth.

**Effort:** one line plus a counter, plus a test that asserts a frame with empty OCR is discarded.

---

## P0-2 · There is no capture indicator at all (INV-4)

**No tray icon exists anywhere in the repository.** No `NotifyIcon`, no `Shell_NotifyIcon`, no taskbar
package. `client/ScreenTail.Core/Shell/TrayPresence.cs:38` computes what a tray icon *would* display and
has zero production callers.

**The HUD is never shown by the running app.** `HudWindow` is constructed in exactly one place —
`client/ScreenTail.UI/Hud/HudPreview.cs:51`, the CI screenshot harness. The normal startup path
(`App.xaml.cs:64-75`) opens `ShellWindow`, which at `Shell/ShellWindow.xaml.cs:25-34` injects a **literal
fake snapshot** (`State = "recording"`, `SessionId = "preview"`).

INV-4 — *capture is always visibly indicated; there is no silent-capture mode* — is the invariant that
makes this product defensible. It is currently unenforced by anything.

**Root cause.** The UI process does not connect to the capture service. `IpcClient` has no production
caller. `ScreenTail.UI` has no `ProjectReference` to the service. The whole UI is, today, a screenshot
harness with real logic behind it.

**Fix.** Wire the UI to the service. This one change also resolves P1-2, P1-6 and three ⚠ entries in the
questionnaire, and gives `WindowsServerVerifier` its caller. It is the single highest-leverage piece of
work left in the project and it is not currently a ticket.

**Effort:** substantial — it is the seam between ST-070 and ST-072.

---

## P0-3 · The HUD hides itself exactly when it does not know whether capture is running (INV-4)

> **Fixed in ST-048.** Hiding now takes effect only when capture is *known* idle, through a `KnownIdle`
> helper that also refuses to guess at an unrecognised state string from a newer service. Two tests cover
> the null case the original test omitted.

**`client/ScreenTail.Core/Hud/HudState.cs:91`**

```csharp
Visible: !hidden || state.Tone is HudTone.Recording or HudTone.Paused or HudTone.Scope);
```

A null `capture` (the UI has not heard from the service) maps to `HudTone.Idle`. So
`For(capture: null, hidden: true)` returns `Visible = false`.

**Scenario.** The technician right-clicks the pill away between sessions — legitimate, permitted. The pipe
then drops, or the service restarts, or a session begins while the UI is reconnecting. The UI does not
know capture stopped, deliberately says so everywhere else ("Capture state unknown — it may still be
recording"), and then **hides the indicator anyway.**

This is my own code, and `HudStateTests.cs:156` tests `hidden` against Recording, Paused and Suppressed
and omits `null` — the one case where the rule matters. The test was written to prove the rule and skips
its most important instance.

**Fix.** Hidden must only take effect when the state is *known* to be idle. Treat unknown as visible.

**Effort:** one clause, one test.

---

## P0-4 · INV-6's scope clause has no test, and deleting it leaves the suite green

> **Fixed in ST-048.** The decision loop moved into `ScreenTail.Core/Capture/SessionRecorder.cs`, behind
> `IScreenshotCapturer` and a `Func<ScopeDecision?>` — the `IForegroundWatcher` pattern this document
> recommended. `ScopeRecordingTests` is the store-level twin of `PausedAndSuppressedWriteNothing`:
> six tests reading the encrypted store, and deleting the drop fails three of them (verified by doing it).
> `ClickCaptureLoop` keeps the hook buffer, the signal reader and the logging, and no longer holds a rule.

`ScreenTail.Tests` references **Core and Shared only**. Twelve `ScreenTail.Service` files have zero test
coverage, including all three sites that actually enforce scope:

- `client/ScreenTail.Service/Capture/ClickCaptureLoop.cs:90-94` — the `!scope.MayRecord(...)` drop
- `ClickCaptureLoop.cs:115`, `SceneSampleLoop.cs:71`, `WindowsCaptureSources.cs:56` — the frame gates

**Delete lines 90-94 and all ~721 tests still pass**, while the product records keystroke counts taken in
a customer's password manager.

The asymmetry is the tell. The `State != Recording` half of INV-6 *is* properly tested at the store
(`StateMachineTests.PausedAndSuppressedWriteNothing`, `PasswordFieldGuardTests` — both genuinely good).
But `CaptureStateReason.ExcludedApp` and `OutOfScope` are **never passed to `SuppressAsync` anywhere in
production**, so those cases rely entirely on the untested loop layer.
`ScopeEnforcementTests.cs:131-140` asserts on a `ScopeDecision` the test constructs itself — a restatement
of the predicate, not a proof that anything is dropped.

**Fix.** Lift the decision loop into Core behind an interface (the pattern `IForegroundWatcher` already
uses), then write the store-level twin of `PausedAndSuppressedWriteNothing`: recording + excluded scope →
exactly one `ClickEvent`, zero typing-derived events, zero frames.

**Effort:** moderate refactor, high value.

---

## P0-5 · The foreground watcher subscribes to ~20 event types and publishes the wrong window

> **Fixed in ST-048.** Two hooks, each asking for one event type, plus a Core allow-list
> (`ForegroundEvents.Interesting`) at the top of `OnWinEvent` as the second line of defence. The range was
> worse than "~20": the inclusive `eventMin`/`eventMax` swept **32,776** event types, which
> `ForegroundEventTests` now measures. The idle CPU test is replaced by one that measures with a window
> moving a hundred times a second, and a new Windows test asserts that a window which only moves is never
> reported as the foreground. **The two Windows tests have not been run yet** — they need the laptop
> (ST-018).

**`client/ScreenTail.Service/Detection/WindowsForegroundWatcher.cs:102-109`**

```csharp
hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND /* 0x0003 */, EVENT_OBJECT_NAMECHANGE /* 0x800C */, ...)
```

Those two arguments are `eventMin` and `eventMax` — an **inclusive range**. This subscribes to everything
from `0x0003` to `0x800C`: the entire `EVENT_OBJECT_*` block (LOCATIONCHANGE, SHOW/HIDE, REORDER, FOCUS,
STATECHANGE), all `EVENT_SYSTEM_*` and all `EVENT_CONSOLE_*`. The comment at line 116 shows two events
were intended.

The only guard (line 164) filters `NAMECHANGE`. **Every other event falls through to
`Publish(_filter.Offer(Read(window)))` with the event's own hwnd** — which is usually not the foreground
window. `ForegroundChangeFilter.Offer` accepts any handle different from the last, so background windows
are published as the foreground and `ScopePolicy.Decide` re-runs on each.

**This is a correctness bug before it is a performance one:** scope decisions (INV-5) and the typing gate
(INV-6) are fed a stream of wrong windows. Frames are still protected because `ScreenshotCapturer`
re-validates the handle, but events are not.

**Why nothing caught it.** `ForegroundWatcherTests.cs:91` measures 20 seconds on a **deliberately idle**
desktop. The 0.000%-of-a-core result is true for that scenario and says nothing about a live session with
a remote-desktop control redrawing under the mouse.

**Fix.** Two hooks (`0x0003-0x0003` and `0x800C-0x800C`), or an event-type allowlist at the top of
`OnWinEvent`.

**Effort:** ~5 lines. Then re-measure on a busy desktop, not an idle one.

---

# P1 — before the pilot

## P1-1 · The egress allowlist is installed on nothing (INV-8)

`EgressGuard.cs:13` claims to be *"the handler every `HttpClient` in the client is built with."* Nothing
in the client constructs an `EgressGuard`, an `HttpClient`, or registers either in DI. The only consumer
that exists — `ModelDownload.cs:102` — builds its request **without a purpose**, so the first time a guard
is wired in, model downloads fail closed and the tempting fix is to hand it an unguarded client.

No egress occurs today because nothing makes HTTP calls. But INV-8 is enforced by convention, which is
exactly what the design says it must not be.

**Fix.** Wire the guard at the composition root; make `EgressRequest.For` the only way to build a request;
add a `ReleaseSurfaceTests`-style scan banning bare `new HttpClient(` in client source (that machinery
already exists).

## P1-2 · The diagnostics panel shows fabricated data

`client/ScreenTail.UI/Diagnostics/DiagnosticsWindow.xaml.cs:25-36` — the parameterless constructor (the
only one used) renders a hard-coded sample: `Local-only: true`, `Egress blocked: 0`,
`Policy: policy-2026-09-13`, `Microphone: Headset Microphone (Realtek)`. Nothing populates it from live
state.

The data-flow document described this as evidence a technician can show a customer. A panel asserting
"local-only: yes" from a literal is worse than no panel. *(The document is corrected; the panel is not.)*

## P1-3 · INV-1 has no test that would notice the masking being removed

Replace `masked.Image` with `frame.Image` at `RedactionWorker.cs:270` — a total INV-1 breach — and
**nothing fails**. No platform-neutral test reads back the redacted bytes; `FakeMasker` and
`PassThroughMasker` both return the input essentially unchanged, and
`AFrameWithACardNumberIsStoredWithItCoveredOver` asserts the *region metadata*, never the covering.

Related vacuities in the same file: `RedactionWorkerTests.cs:87-88,100` assert `Assert.Empty(...Frames)`
after a discard, but `LoadSessionAsync` filters pending frames — so a frame left sitting unredacted passes
the same assertion.

**Fix.** A masker fake that actually mutates, and an assertion that the stored bytes differ from the
staged bytes.

## P1-4 · 63% of the hardware suite can vanish green, and it is not a required check

Of 46 hardware facts, **18 always skip on the hosted runner and up to 29 can skip.** `dotnet test` exits 0
when everything skips, and the `client` job has no skip gate.

Worse: `hardware-checks.yml` is behind `if: vars.HW_RUNNER == 'true'` and is **not in `ci-ok`'s `needs`**
— it is not a required check at all. Its `paths:` filter omits `Store/**` and `Ipc/**`.

**`TypingAPasswordRecordsOnlyHowManyKeys` — INV-2's only end-to-end proof — runs in exactly one place: an
optional, unrequired job.**

The `NO-DESKTOP` canary added on 2026-09-13 is blind to the ten tests most at risk:
`ForegroundWatcherTests` and `ScreenshotTests` gate on the capability probe and return *before*
constructing a `DesktopWindow`, so no marker is written and the guard reports success.

**Fix.** Emit `--report-trx`, parse the skip count, fail above a baseline. Everything else in this document
is worth less while the suite can disappear silently.

## P1-5 · The audit log can be truncated from the tail undetectably

`SqliteSessionStore.cs:693-719` walks rows in `id` order checking each `prev_hash`. Removing the **last N
rows** leaves a chain that verifies perfectly — there is no head anchor, no row count, no high-water mark.
`AuditChain.cs:18` claims the chain detects "a log truncated to hide a purge"; it detects truncation from
the front, not from the back, which is the direction anyone hiding recent activity would cut. The row `Id`
is also not in the hash input, so ids can be renumbered freely.

T11 (repudiation) rests on this.

## P1-6 · Sixteen idle connections lock the UI out of the capture service

`IpcServer.cs:175-181` caps in-flight handshakes with one global counter, no per-peer accounting and no
fairness. A same-user process — explicitly in scope — opens 16+ connections, sends nothing, and refills
every slot as the 5 s timeout reaps them. The real UI sees the pipe closed during handshake. No token and
no signed binary required.

Effect: the technician cannot see or change capture state, which defeats INV-4 by making the indicator
unreachable. The threat model (T12) claims "one client at a time"; `IpcServer` accepts unbounded
authenticated clients.

## P1-7 · The SQLCipher key survives in immortal managed strings

`SqliteSessionStore.cs:898-912` builds `PRAGMA key = "x'{hex}'"` by interpolation. `OpenAsync:77` and
`DpapiKeyProvider:61` both carefully `Array.Clear` their byte copies — but `hex` and the `CommandText` are
immutable strings that live until GC, and beyond that in a crash dump or page file. Two full-fidelity
copies of the store key outlive the hygiene written to prevent exactly that. T4 (laptop theft) is the
store's headline threat.

## P1-8 · Threat-model claims contradicted by the code

- **T8** says the IPC token is *"never written to disk."* `CaptureHost.cs:56` writes it to
  `%LOCALAPPDATA%\ScreenTail\ipc.token`. It is ACL'd to the user — defensible, and necessary for the UI to
  read it — but the document asserts a stronger control than exists.
- **T12** says *"one client at a time."* `IpcServer` documents itself as accepting any number.
- **T10** lists `EgressPolicy`/`EgressGuard` as the shipping mitigation with no "not yet wired" caveat,
  unlike T1 which is correctly caveated.

## P1-9 · `_givenUp` grows without bound and is expanded into SQL on every poll

`RedactionWorker.cs:80,120,154` — never cleared for the life of the service, unioned into the skip set on
every `ProcessOneAsync` (10×/second across two workers), and handed to `SqliteSessionStore.cs:166-184`
which builds `AND id NOT IN (@skip0, …)` with one parameter each.

A persistent failure cause — an invalid tenant regex (`PatternLibrary.cs:93` catches
`RegexMatchTimeoutException` but not the `ArgumentException` from a bad pattern) — puts every frame in the
set. The statement grows until `SQLITE_LIMIT_VARIABLE_NUMBER`, then throws on every call,
`StepAsync:206-215` swallows it into `_failed++`, and **redaction is silently dead for the rest of the
service's life while frames keep being staged raw.**

---

# P2 — real cost, schedule deliberately

| # | Finding | Where | Cost |
|---|---|---|---|
| P2-1 | ~~**The pattern library runs twice per frame.**~~ **Fixed in ST-048:** `RedactFrame` resolves once and the stored text is written from the same matches that placed the mask boxes. `PatternEngineTests` counts the passes and asserts one. The throughput test's cadence assertion, which shrank to nothing as redaction got faster, now measures over a fixed four-second window. | `RedactionEngine.cs` | Was a straight 2× on a path measured at 2012 ms worst case against a 700 ms budget. **The new median is not yet measured — it needs the laptop.** |
| P2-2 | **4K frame staging is 2.5× over budget and unasserted.** The test asserts a 1.37 MP window (62 ms, passes); the 4K extrapolation of **309 ms against 120 ms** is recorded and never asserted. | `ScreenshotTests.cs:78-93` | Every frame on a 4K monitor blows the budget; nothing fails. |
| P2-3 | **The filmstrip decodes ~230 MB to draw 40 thumbnails.** `byte[]` binding decodes at native size (no `DecodePixelWidth`), the panel is a bare non-virtualizing `WrapPanel`, and every frame is loaded up front with no eviction. | `FilmstripView.xaml:22`, `Components.xaml:416`, `FilmstripViewModel.cs:135` | ~5.76 MB per 160×90 thumbnail. 150 frames ≈ 860 MB and an OOM. Plus 200-600 ms of UI-thread jank. |
| P2-4 | **Hourly `VACUUM` of the whole encrypted store, on the connection capture writes through.** | `RetentionJob.cs:31-34`, `SqliteSessionStore.cs:521` | Multi-GB rebuild, every page decrypted and re-encrypted, with capture blocked behind `_gate`. Fires mid-working-day on a 7-day retention. |
| P2-5 | **Each frame is decoded twice and its bytes copied four times.** `ToArray()` on parameters that are already `byte[]`. | `WindowsOcrRecogniser.cs:61,65`, `WindowsFrameMasker.cs:32,33,86` | ~80 MB transient per 4K frame × 2 concurrent. Explains much of the gap between 185 ms median and 2012 ms tail. |
| P2-6 | **Audit log: N+1 writes, an index-defeating filter, unbounded growth.** `WHERE (@session IS NULL OR session_id = @session)` cannot use the index; nothing ever deletes an audit row. | `SqliteSessionStore.cs:641,663` | Full scan of the lifetime log to read one session. Largest table in the store over time. |
| P2-7 | **Capabilities fully re-probed per IPC request** — including installing and removing a **global low-level mouse hook**. | `CaptureController.cs:15`, `WindowsCapabilityProbe.cs:161` | Latent: becomes periodic global-hook churn the moment anything polls it. |
| P2-8 | **No backpressure on frame staging.** Frames are staged at native resolution (4K ≈ 3-6 MB each); the only bound is disk. | `SessionMachine.cs:265` | At the redaction tail, two workers manage ~1 frame/s against ~2.5/s of clicking. Backlog is multi-megabyte rows. |
| P2-9 | **`FrameBlurrer` re-encodes a JPEG frame as PNG, synchronously, on the UI thread.** | `FrameBlurrer.cs:48` | 5-15× blob growth per blurred frame, and the stored name `frames/{id}.jpg` becomes a lie. ~100-200 ms frozen UI. |
| P2-10 | **Filmstrip image loading is a serial N+1** against the store, each taking `_gate`. | `FilmstripViewModel.cs:135` | 40 round trips serialized against live capture writes. |
| P2-11 | **Missing index, and the budget test that guards it seeds one frame per session.** `ListSessionsAsync`'s correlated count has no `(session_id, redaction_pending)` index. | `SqliteSessionStore.cs:597`, `SessionHistoryTests.cs:114` | The 200-session/500 ms test does 200 lookups instead of ~30,000 — it cannot fail for the reason it exists. |
| P2-12 | **Deleted frames leave recoverable ciphertext.** No `PRAGMA secure_delete`, no vacuum on discard. | `SqliteSessionStore.cs:291,489` | `ISessionStore` says "deletes for good"; freed pages keep the old blobs until reused. Matters in the T4 laptop-theft case. |
| P2-13 | **A same-user process can permanently prevent the service starting** by squatting the deterministic `Local\` mutex name; the service exits code 3 silently. | `SingleInstance.cs:16` | Capture never runs, nothing says why. |

---

# P3 — correct when convenient

**Vacuous or weak tests** (full list in the review transcript; the ones with teeth removed):
`IpcFramingTests.cs:47` (oversize refusal — the same exception is thrown by the next guard),
`IpcServerTests.cs:81`, `EgressGuardTests.cs:181` (asserts on an exception the test itself constructed),
`ModelDownloadTests.cs:33-115` (six `Assert.True(true)` — `EnsureAsync` has no `false` path),
`SpeechGateTests.cs:66` (**set `LeaveAt = EnterAt` and the suite still passes** — the hysteresis band is
never exercised), `SceneSamplerTests.cs:170`, `TrayAndDiagnosticsTests.cs:78` (restates the constructor),
`DpapiKeyProviderTests.cs:22,46`, `ScreenshotTests.cs:36,58` (**a fully black 1080p capture passes the
"not blank" check — 33 KB against a 2 KB floor**; this is the exact check already replaced in `ci.yml`),
`OcrTests.cs:218` (single centre pixel — passes for shrink-then-paint, the ordering bug the masker's
comment calls "the whole correctness argument"), `test_fixtures.py:130` (guard can never fire).

**Tolerance bands that cannot fail:** `ScreenshotTests.cs:175` tolerates 0.98 leaked handles per capture
(disaster is 1.0); `SessionStoreTests.cs:249` would pass a store keeping the unredacted original alongside
the redacted copy — an INV-1 regression the disk-budget test cannot see; `RedactionThroughputTests.cs:118`
**self-disarms as it gets faster**; `ContrastTests.cs:45` reads its own thresholds from the artifact under
test, so lowering `minimum` to 2.0 makes every failing colour pass.

**Still shipping: the distinct-colours gate.** `ci.yml:125,143,168` and `WindowRenderer.cs:145` detect
"rendered nothing" only. A pane in entirely the wrong colours clears the threshold by ~50×. This is the
bug already caught once on the HUD; the `SaveExpecting` fix was applied there and nowhere else. The
binding-error gate beside it is genuinely strong.

**`ManualTime` does not override `CreateTimer`.** `SessionMachine.cs:369` awaits `Task.Delay(..., _time,
ct)`, which routes through it — so a future test injecting `ManualTime` into `SessionMachine` will wait
real wall-clock while `Advance()` does nothing. Exactly the class of bug the shared file was written to
prevent. Three byte-identical private copies of `ManualTime` also still exist.

**Dead parameter:** `ScreenshotCapturer.cs:41` takes `maxEdge` and never uses it; four call sites pass it
believing it downscales.

**Documentation drift** *(as found 2026-09-15; corrected the same day in the housekeeping PR)*: all three ADRs were still marked **Proposed** after their tickets merged, ADR-0001 still argued from .NET 8 against a .NET 10 project, and `STATUS.md` claimed 26 tickets and 586 tests against 35 and 721. The ADRs now say Accepted with the date, the .NET 8 residue is gone from the ADR, the spike README and the test-loop doc, and ticket state lives on a **Status** line per ticket in the backlog (Part D, D-5). The findings above are now tickets: P0-1/P0-3/P0-4/P0-5/P2-1 → **ST-048**; P1-4 → **ST-018**; P0-2/P1-1/P1-2/P1-6 → **ST-085**; the rest of P1 and the scheduled P2s → **ST-049**.

**Dead on arrival.** Entire namespaces are written, tested, documented as shipping, and called by nothing:
`Core/Net` (all of it), `Core/Speech` (all of it), `Core/History`, `Core/Timeline`, `Core/Intel`,
`Core/Audit`'s export half, `TrayPresence`, `LocalDataEraser` ("delete everything" is unreachable — no
Settings screen, no IPC command), `ReviewSession`, `IpcClient`, `WindowsServerVerifier`, and seven
`ISessionStore` read methods. Most of this resolves with P0-2.

---

# What is genuinely strong

Worth recording so it does not get "fixed":

- **INV-2 is enforced structurally**, not by discipline. `InputSignal` has no field that can hold a key;
  `WindowsInputHooks.Classify` reduces the VK inside the callback and lets it go out of scope. The schema
  independently forbids it. This is the strongest guarantee in the codebase.
- **INV-1's read-path filtering is thorough.** Every `frames` read filters `redaction_pending = 0`; the
  one unfiltered read has a single caller; no error path returns raw bytes. (P0-1 is a *write*-path bug —
  the filter is correct, the flag is set too early.)
- **No SQL injection anywhere.** Every statement is a hard-coded literal with bound parameters; the two
  dynamic fragments build only placeholders from loop indices.
- **`Authenticode`** verifies before reading the thumbprint, closes the trust state on both paths, frees
  both allocations in `finally`, and its comment accurately describes the forgery it replaced.
- **Scene sampling and detection are allocation-disciplined**: stack-allocated hashes, one 272-byte array
  per grid, regexes compiled once with timeouts, a cheap-grid-before-expensive-capture design that is why
  the 1 Hz sampler costs nothing.
- **The best tests in the repo**: `PasswordFieldGuardTests` (which explicitly names and sidesteps the
  vacuity trap), `ATamperedCopyOfASignedBinaryIsRefused`, `PatternLibraryGapTests`, the audit tamper tests
  (which mutate the encrypted table out of band), `ReleaseSurfaceTests.TheseTestsAreLookingAtRealFiles`,
  and `test_wer.py`.

---

# Suggested order of work

1. **P0-1** (one line) and **P0-3** (one clause) — both are small, both are invariant breaches.
2. **P0-5** (~5 lines) — correctness and a CPU firehose, then re-measure on a busy desktop.
3. **P2-1** (~10 lines) — halves the redaction budget you are already over.
4. **P1-4** — the skip gate. Until this exists, none of the hardware evidence means anything.
5. **P0-4** — the scope-enforcement test, with the refactor it needs.
6. **P0-2** — wire the UI to the service. Large, and it closes P0-2, P1-1, P1-2, P1-6 and three ⚠ answers.
7. Everything else, in band order.

Items 1-3 are roughly twenty lines of production code between them and address two invariant breaches and
the largest performance lever in the product.
