# The end-to-end run: the whole loop on Windows, with nobody at the keyboard

**What this is for.** One command that takes a session through all of ScreenTail on a real Windows
machine and says, step by step, which part of the loop works and which does not: the backend, a device
token, the capture service, the UI and its recording pill, a window being captured and redacted, the draft,
the publish, and what the PSA received. It is ST-013's harness, and it runs in CI on every pull request
that touches the client, the backend or the Windows scripts.

**What it is not.** A test of a model's note. It drafts with the **scripted test provider**, which writes
a fixed note from the bundle's own redacted text (below), so it needs no key and costs nothing. Whether
Gemini's note is any good is M1's question and the eval corpus's, not this run's. It also does not publish
to ConnectWise: the note lands on the backend's **demo PSA** (`docs/dev/demo-psa.md`).

## Run it on the laptop (one command)

Once, if it has not been done: `scripts\windows\demo\setup.cmd` (the development certificate and the
database server). Then, from your clone, in a normal (not administrator) PowerShell:

```powershell
scripts\windows\e2e\run-e2e.cmd
```

About four minutes the first time (it builds the backend and publishes the client), two after. It ends
with `PASS e2e: all N steps passed` or `FAIL e2e: ...`, and exit code 0 or 1.

- It uses the database server demo setup saved, in a database of its own (`screentail_e2e`), and the
  backend on port **5071**, so the demo's database and port 5001 are untouched. Stop the demo first
  (`scripts\windows\demo\stop.cmd`): one capture service per user.
- It runs the real capture service, so it uses the product's own data folder
  (`%LOCALAPPDATA%\ScreenTail`): the service has no setting for another one, and adding one only for a
  test was not worth a second way to find the store. The test session stays in History afterwards, with
  its note. It turns **All windows** on for the session and puts it back twice over: the driver restores
  the setting over the pipe at the end or on Ctrl+C, and the runner copies `capture.json` aside before it
  starts anything and puts it back, byte for byte, in a `finally` that also stops every process it
  started, whatever failed or was interrupted.
- It shows a white test window with a few lines of made-up text for about a minute. Leave the mouse alone.
- `-InjectClicks` drives the session with real input instead of pipe commands: the start and stop
  hotkeys (Ctrl+Alt+R and Ctrl+Alt+S, or whatever Settings binds them to) and three real left clicks on
  the test window. It takes over the keyboard and mouse, so it also needs
  `SCREENTAIL_ALLOW_INPUT_INJECTION=1`, as the laptop's input job sets:
  `$env:SCREENTAIL_ALLOW_INPUT_INJECTION = '1'; scripts\windows\e2e\run-e2e.cmd -InjectClicks`.
- A laptop with an invite-activated device (`%LOCALAPPDATA%\ScreenTail\device.cred`) is refused, because
  the service prefers that device to a development token and the test database does not know it. See
  "Activated device" in `docs/dev/demo.md`.

The logs go to `%LOCALAPPDATA%\ScreenTail.Dev\e2e`: `e2e.log` (every PASS and FAIL line), and the
service's, the UI's and the backend's own logs. They hold no content (both processes log through the scrubber,
INV-10) and no secret; the last step checks that.

## In CI

`ci.yml`'s **End to end (Windows)** job runs `scripts/windows/e2e/run-e2e.ps1` on the hosted
`windows-latest` image with Windows PowerShell 5.1, when a pull request changes `client/`, `backend/` or
`scripts/windows/`, and on `workflow_dispatch` (`gh workflow run ci.yml --ref <branch>`). On the hosted VM
it starts the image's own PostgreSQL service (disabled by default; its credentials are in `PGUSER` and
`PGPASSWORD`) and creates and trusts the development certificate for the machine, which the VM is thrown
away after. It uploads only `*.log` and `*.txt` from the run, copied into a clean folder by name, as the
laptop's jobs do; there is no image in the run's output to upload.

**It is report-only for now**: `continue-on-error`, and not one of `ci-ok`'s needs. Once it has passed
twice in a row on `main`, remove `continue-on-error` and add `e2e` to `ci-ok`'s needs in one pull request.

The hosted VM has a desktop but no input injection, so there the session is started and stopped over the
pipe (the onboarding wizard's test session sends the same start) and its frames come from **mark
moment**, the command the pill's own button sends, plus whatever the scene sampler picks up.

**On the laptop**, `hardware-checks.yml`'s input job runs the same script with `-InjectClicks` after its
input tests: the hotkeys start and stop the session through the coordinator, as in the demo's way B, and
real clicks take the frames through the click loop. It is report-only too (`continue-on-error`), adds no
test to the skip gate, and uploads nothing; its PASS and FAIL lines are in the job's log. It is capped at
eight minutes with the draft waited for two (`-DraftTimeout 120`), so the input job keeps its fifteen and
the two laptop jobs stay well inside the hardware gate's thirty-five, and it does not run on a cancelled
run, so a superseded run does not go on driving the keyboard and mouse. It needs
`scripts\windows\demo\setup.cmd` to have been run once on the laptop, for the database server and the
certificate.

## What each step proves

Every line reads `PASS|FAIL <phase>-<nn> <name> (<seconds> s): waited for <what>. <what it saw>`. A
failure names the join of the loop that broke and what was there instead.

**Setup** (`run-e2e.ps1`)

| Step | Proves |
|---|---|
| setup-01 windows, 02 dotnet | Windows PowerShell 5.1 in a clone, a .NET 10 SDK global.json accepts |
| setup-03 nothing-running | No other ScreenTail, no activated device, the port is free |
| setup-04 postgres | PostgreSQL answers (the runner's service, or the server demo setup saved) |
| setup-05 certificate | The development certificate is trusted the way the client checks it |
| setup-06 backend-build, 07 migrations | The backend builds, and every migration applies to a fresh database (zero on a rerun) |
| setup-08 backend | The backend answers `/health` over HTTPS in Development, with `Providers:Demo=true` and `Summarization:Provider=scripted` |
| setup-09 enrol | `--enrol-dev-device` gives a token the backend accepts at `/v1/me` |
| setup-10 client-publish | The service, the UI and the driver publish into one folder (the unsigned handshake's rule) |
| setup-11 service | The capture service starts with `SCREENTAIL_BACKEND` and the token in its own environment only, publishes its pipe token and says it started |
| setup-12 ui | The UI starts and stays up |

**The loop** (`client/ScreenTail.E2E`, through the pipe commands the UI sends)

| Step | Proves |
|---|---|
| loop-01 pipe | The service lets a same-folder client in (ADR-0003) |
| loop-02 ui | The UI process is there |
| loop-03 all-windows | Settings, Capture saves "All windows" through `set_capture_settings`, as the Capture pane does |
| loop-04 test-window | A window with known text, and a line `pass: <made-up secret>`, takes the foreground |
| loop-05 scope | The foreground watcher and the scope policy say the window is captured |
| loop-06 start | A start over the pipe (with `-InjectClicks`, the start hotkey) is accepted, which the service allows only with a current indicator report from the UI. **The driver never sends one itself**: this is the real pill's report |
| loop-07 pill | The pill is on a monitor, and capture stays recording for twelve seconds, longer than a report lasts plus the guard's grace (INV-4) |
| loop-08 capture | Mark moment twice, the window's text changing between (or real clicks with `-InjectClicks`) |
| loop-09 redaction | The redaction worker drains the queue; frames dropped unread are counted |
| loop-10 stop, 11 draft | Stop and draft (or its hotkey) is accepted; the session rests in `draft_pending` while its draft is queued, and reaches `draft_ready` when the outbox delivers the backend's note. `draft_failed` is a FAIL with its reason |
| loop-12 stored-frames | Every stored frame is redacted (INV-1), and a password mask covers the `pass:` line |
| loop-13 frame-text | The stored OCR text has the window's words and `[REDACTED]`, and never the secret |
| loop-14 note | The note is the scripted provider's, cites stored frames, and `draft_ready` carries its title for the notification |
| loop-15 integrations, 16 ticket | The backend reports the demo PSA, and the search finds ticket #48213 |
| loop-17 publish | `publish_session` to #48213 succeeds, as Review's Publish sends it |
| loop-18 landed | The demo PSA's read endpoint (`/dev/demo-psa/tickets/48213`) shows the note: the window's words, the pass line masked, the screenshot count, and no secret (what the backend received; images are not kept, INV-7) |
| loop-19 audit | The audit export (collected the UI's way) has this session's `frame_captured`, `frame_redacted` (password) and `bundle_sent` rows, and its chain is intact |
| loop-20 restore | Settings, Capture is put back as it was, whatever failed above |

**Teardown** (`run-e2e.ps1`)

| Step | Proves |
|---|---|
| teardown-01 ui-alive | The UI lasted the whole session |
| teardown-02 service-stop | Ctrl+Break makes the capture service stop cleanly: exit code 0, and it logged that it was stopping |
| teardown-03 ui-outlives-service | The UI survives the service going away (its Quit is a tray click, so it is closed after) |
| teardown-04 backend-stop | The backend stops cleanly the same way |
| teardown-05 no-crash | Neither process left a crash report (crash reports are switched on for the run) |
| teardown-06 no-secret-in-logs | The run's made-up secret, or any five-letter run of it, is in no log (the backend's, the service's, the UI's, the runner's) and no crash report |

## The scripted test provider

`Summarization:Provider=scripted` (backend, `Providers/Llm/ScriptedProvider.cs`). Development only: the
backend refuses to start with it anywhere else, as it refuses `Providers:Demo`. Its note has one step per
screenshot carrying the device's own masked OCR text, with quotation marks taken out, a line for the
transcript segments, and "drafted by the scripted test provider" in its problem and its result. It costs
nothing and goes through the same validator as a model's note; if the validator refuses a screen's text,
the repair keeps only the counts.

It is also how to rehearse the demo offline, or past the free Gemini key's daily limit: start the backend
with `$env:Summarization__Provider = 'scripted'` added. The note it writes says what it is, so it is not
something to show an audience as ScreenTail's drafting.

## The secret

Each run makes up its own fake password (two words and four digits), so nothing secret-shaped is in the
repository and a match cannot be a coincidence. The runner hands it to the driver in its environment; it
is never printed. The driver puts it on the test window after `pass:`, checks it is masked in the stored
text, the note and what the PSA received, and the last teardown step searches every log for it.
