# Service–UI IPC contract (v2)

The capture service and the UI talk over a named pipe (`ScreenTail.<user-sid>`; on macOS/Linux dev boxes a Unix socket of the same name). Design and security rationale: [ADR-0003](adr/0003-process-hosting-and-ipc.md). Types: `client/ScreenTail.Shared/Ipc/`. Transport: `client/ScreenTail.Core/Ipc/`.

**Last brought into line with the code: 2026-09-22.** `IpcContract.Version` is **2**, since 2026-09-14 (ST-072, #46): `reason` and `scope_process` were added to the state snapshot, and because the framing rejects unknown members an older reader would have failed on a newer snapshot rather than ignored the field. Everything since has been new commands and events an older client never sends or receives, and has not bumped it.

## Framing

Each message is one frame: a 4-byte little-endian unsigned length, then that many bytes of UTF-8 JSON. Maximum 1 MiB; a larger length closes the connection. No message is ever split across frames or batched.

## Handshake

1. The client connects and sends `hello`.
2. The service verifies the connection (same user, verified executable, correct token, matching `contract_version`).
3. On success it replies `hello_ack` with the current state; on failure it replies `rejected` and closes the pipe. Any other first message also gets `rejected`.

`IpcContract.Version` is bumped on any change an older reader would fail on; a `hello` with a different version is rejected with reason `contract_mismatch`. New command and event types don't bump it: a client that never sends one never sees its reply. A new field on a message both sides already exchange does, because the framing rejects unknown members on purpose.

## Commands (UI → service)

Every command carries `request_id`, an integer the client chooses. `hello` is answered by `hello_ack` (or `rejected`); every other command is answered by a `result` carrying the same `request_id`. A `get_state` also sends the requester a `state_changed` before its `result`.

| `type` | Fields | Meaning |
|---|---|---|
| `hello` | `contract_version`, `token`, `client_name`, `client_version?` | Authenticate the connection |
| `start` | — | Start a session now (manual start, Ctrl+Alt+R) |
| `pause` | — | Pause capture |
| `resume` | — | Resume capture |
| `stop` | — | Stop and draft |
| `discard` | `confirmation` | Stop and delete the current session's raw data. Refused without a token issued for `discard_session` (below) |
| `mark_moment` | — | Force a frame and a marker event |
| `get_state` | — | Ask for the current state (also arrives in `hello_ack`) |
| `get_capabilities` | — | Re-run the Windows checks (ST-021). Answered by `capabilities` |
| `get_diagnostics` | — | What is being captured right now (ST-071). Answered by `diagnostics` |
| `list_sessions` | `limit?` (default 200) | The History screen's rows (ST-079). Answered by `sessions` |
| `request_confirmation` | `action` (`discard_session` or `erase_everything`) | Ask for a one-use token before something irreversible (ST-085). Answered by `confirmation` |
| `erase_all_local_data` | `confirmation` | Delete everything and stop (INV-12). Refused without a token issued for `erase_everything`. The pipe drops straight after: the store it was serving is being deleted |
| `indicator_showing` | `x`, `y`, `width`, `height` | The UI saying the recording pill is on screen, and where (INV-4). Sent every two seconds while it is true; the service forgets a report it has not heard repeated within six seconds |

## Events (service → UI)

| `type` | Fields | When |
|---|---|---|
| `hello_ack` | `contract_version`, `service_version`, `state` | Handshake succeeded |
| `rejected` | `reason` (`unverified_client`, `bad_token`, `contract_mismatch`, `protocol`) | Handshake failed; the pipe closes after |
| `result` | `request_id`, `ok`, `error?` | Reply to a command |
| `state_changed` | `state` | Capture state changed; sent to every connected client |
| `capabilities` | `checked_at`, `can_capture`, `checks[]` | Answers `get_capabilities`; also sent when a check changes |
| `diagnostics` | see below | Answers `get_diagnostics` |
| `sessions` | `sessions[]` | Answers `list_sessions` |
| `confirmation` | `token`, `phrase` | Answers `request_confirmation` |

**Every event may carry a `request_id`**, and one does whenever it answers a command. The client completes
the pending request whose id matches, whatever the event's type; only `state_changed` is ever volunteered.
Before ST-085 the client completed a pending request on a `result` alone, so `capabilities` — which has had
a reply type since ST-021 — would have hung the first time anything asked.

A question is answered with its own event **and then** a `result`, so a client that understands only
results still learns the command was accepted.

### The state snapshot

`state` is a `CaptureStateSnapshot`:

| Field | Meaning |
|---|---|
| `state` | `idle`, `recording`, `paused`, `suppressed`, `finalizing`, `draft_ready`, `draft_failed`. ST-020's `SessionMachine` owns the transitions |
| `session_id?`, `elapsed_ms?` | The session, and its active time excluding pauses |
| `remote_tool?` | Which tool started it |
| `reason?` | Why capture is not running when the state alone does not say: `password_field`, `excluded_app`, `elevated_window`, `sensitive_context`, `out_of_scope`, `user` (ST-072; Spec §5 S2 gives each its own words) |
| `scope_process?` | The process in front when capture is out of scope, so the HUD can say "Not capturing — Outlook". A process name, never a window title (INV-10) |
| `pending_redactions`, `drafts_ready` | Counts |
| `draft_failure_reason?` | Only in `draft_failed`. Plain language, no content |
| `draft_title?`, `draft_company?` | Only in `draft_ready`, for the notification (Spec §6). Model-written text that crosses the pipe to the same user's UI and nowhere else |

### `get_capabilities` → `capabilities`

ST-021. The service re-runs the checks on each request rather than answering from a cache: a technician can
revoke microphone access halfway through a working day, and a report that says otherwise is worse than none.

```json
{ "type": "capabilities", "request_id": 4, "checked_at": "2026-09-12T18:00:00Z", "can_capture": true,
  "checks": [ { "capability": "microphone", "state": "blocked",
                "message": "Microphone is blocked by Windows privacy settings.",
                "fix_hint": "Allow desktop apps to use your microphone, then start the session again.",
                "fix_link": "ms-settings:privacy-microphone" } ] }
```

`capability` is one of `desktop_session`, `microphone`, `screen_capture`, `input_hooks`, `elevated_windows`;
`state` is `ok`, `degraded`, `blocked` or `unknown`. `unknown` means the check itself failed and is treated as
blocking — "we couldn't tell" must never read as "fine". `can_capture` is false when any of the desktop
session, screen capture or input hooks is blocked; a blocked microphone costs narration, not the session.
`fix_link` is a Windows Settings deep link for the HUD's "Fix" button (ST-072). Messages describe Windows
settings only and never contain anything captured (INV-10).

### `get_diagnostics` → `diagnostics`

ST-071's panel, answered by the service because the UI has no store, no hooks and no counters of its own.

```json
{ "type": "diagnostics", "request_id": 7, "scope": "Capturing - ScreenConnect",
  "microphone": "Headset Microphone (Realtek)", "suppression": "password_field",
  "redaction_backlog": 4, "frames_dropped": 0, "keystrokes_dropped": 17, "egress_blocked": 0,
  "local_only": true, "policy_version": "local", "cpu_percent": 3.4,
  "working_set_bytes": 251658240, "service_version": "0.1.0" }
```

Every field is a state, a count or a device name. `scope` is the scope decision's own words — a process
name and what is being done about it — never a window title, because this text goes on a clipboard and
into tickets (INV-10). `suppression` is absent when nothing is suppressing capture. `redaction_backlog`
is the depth of the in-memory queue (ADR-0006); `frames_dropped` counts frames deleted because they
could not be checked (INV-1). `local_only` and `egress_blocked` come from the same policy object the
guard enforces with, so the panel cannot say one thing while the guard does another.

### `list_sessions` → `sessions`

```json
{ "type": "sessions", "request_id": 8, "sessions": [
  { "id": "s1", "started_at": "2026-09-16T09:00:00Z", "duration_ms": 61000, "status": "draft",
    "tool": "screenconnect", "frames": 7, "frames_purged": 1 } ] }
```

`status` is one of `draft`, `published`, `discarded`, `partial`, `pending`, computed by the service so
that the draft's text never crosses the pipe just to be turned back into a single word. `frames` counts
redacted frames only: a pending frame is not one anything outside the redaction worker may count (INV-1).

### `request_confirmation` → `confirmation`, then `discard` or `erase_all_local_data`

ST-085. Spec §3 has asked for a typed confirmation before an irreversible delete since it was written;
until 2026-09-20 the tray menu threw a session away on a single click, one item below "Stop and draft".

```json
{ "type": "request_confirmation", "request_id": 9, "action": "discard_session" }
{ "type": "confirmation", "request_id": 9, "token": "…", "phrase": "discard" }
{ "type": "discard", "request_id": 10, "confirmation": "…" }
```

The token is good once, for that action only, for 30 seconds. `phrase` is what the technician has to
type to mean it. **The second round trip is the defence:** a confirmation the UI is trusted to have shown
is one a process that is not the UI does not have to show, and the pipe only proves the peer is the same
user. The service binds the token to the connection that asked for it and keeps at most eight
outstanding; a `discard` or `erase_all_local_data` without a valid token gets `ok: false`.

### `indicator_showing`

The UI's claim that the recording pill is visible, with the pill's own rectangle in virtual-screen
coordinates, so the claim is specific enough to check against a screenshot. It is sent while it is true
and not once at startup, because the interesting failure is a UI that stops painting without closing: the
socket stays open and the pill goes stale. A report expires after six seconds, and a connection is not
an indicator — the service counts pills it has heard from, not clients it can see (INV-4, #130). While
none is reported, `IndicatorGuard` suppresses capture after a short grace for UI restarts.

## Authentication

Details in ADR-0003. In short: the pipe admits only the same user; the service checks the client executable (signed by the same publisher, or same directory for unsigned dev builds); the UI checks the pipe server the same way before writing the token; and the client presents the per-service-run token from `%LOCALAPPDATA%\ScreenTail\ipc.token`, a file only that user can read. Rejections are audit-logged as `ipc_rejected_<reason>`, nothing else.

## Reattach

The service keeps all state. A reconnecting client sends `hello` and gets the state back in `hello_ack`. Budget: 2 s from UI restart to reattached; measured well under 100 ms.

## Connection limits

The handshake table holds 16 in flight and a peer has two seconds to say `hello`. When the table is full
the **oldest silent** handshake is dropped to make room, and the newcomer is never refused. Refusing the
newcomer was the earlier policy and it handed a same-user process a better attack than the one it fixed:
sixteen silent connections kept the table full, every refusal fell on whoever asked next, and the real UI
was the one turned away — unable to see or change capture state, which defeats INV-4 by making the
indicator unreachable rather than by touching capture.

## Changes

| Date | What | Version |
|---|---|---|
| 2026-09-12 | `get_capabilities` / `capabilities` (ST-021) | 1 |
| 2026-09-14 | `reason` and `scope_process` on the snapshot (ST-072, #46) | **2** |
| 2026-09-15 | `get_diagnostics` with `suppression`, `list_sessions`, `erase_all_local_data`; every event may carry `request_id` (ST-085, #59) | 2 |
| 2026-09-18 | `draft_title`, `draft_company` on the snapshot (ST-073, #66) | 2 |
| 2026-09-20 | `request_confirmation` / `confirmation`; `confirmation` on `discard` and `erase_all_local_data` (#86) | 2 |
| 2026-09-22 | `indicator_showing`; the oldest-silent handshake rule (#130) | 2 |
