# Service–UI IPC contract (v1)

The capture service and the UI talk over a named pipe (`ScreenTail.<user-sid>`; on macOS/Linux dev boxes a Unix socket of the same name). Design and security rationale: [ADR-0003](adr/0003-process-hosting-and-ipc.md). Types: `client/ScreenTail.Shared/Ipc/`. Transport: `client/ScreenTail.Core/Ipc/`.

## Framing

Each message is one frame: a 4-byte little-endian unsigned length, then that many bytes of UTF-8 JSON. Maximum 1 MiB; a larger length closes the connection. No message is ever split across frames or batched.

## Handshake

1. The client connects and sends `hello`.
2. The service verifies the connection (same user, verified executable, correct token, matching `contract_version`).
3. On success it replies `hello_ack` with the current state; on failure it replies `rejected` and closes the pipe. Any other first message also gets `rejected`.

`IpcContract.Version` is bumped on any breaking change; a `hello` with a different version is rejected with reason `contract_mismatch`. Additive changes (new optional fields, new event types the client may ignore) don't bump it.

## Commands (UI → service)

Every command carries `request_id`, an integer the client chooses. `hello` is answered by `hello_ack` (or `rejected`); every other command is answered by a `result` carrying the same `request_id`. A `get_state` also sends the requester a `state_changed` before its `result`.

| `type` | Fields | Meaning |
|---|---|---|
| `hello` | `contract_version`, `token`, `client_name`, `client_version?` | Authenticate the connection |
| `start` | — | Start a session now (manual start, Ctrl+Alt+R) |
| `pause` | — | Pause capture |
| `resume` | — | Resume capture |
| `stop` | — | Stop and draft |
| `discard` | — | Stop and delete the current session's raw data |
| `mark_moment` | — | Force a frame and a marker event |
| `get_state` | — | Ask for the current state (also arrives in `hello_ack`) |
| `get_diagnostics` | — | What is being captured right now (ST-071). Answered by `diagnostics` |
| `list_sessions` | `limit?` | The History screen's rows (ST-079). Answered by `sessions` |
| `erase_all_local_data` | — | Delete everything and stop (INV-12). The pipe drops straight after: the store it was serving is being deleted |

## Events (service → UI)

| `type` | Fields | When |
|---|---|---|
| `hello_ack` | `contract_version`, `service_version`, `state` | Handshake succeeded |
| `rejected` | `reason` (`unverified_client`, `bad_token`, `contract_mismatch`, `protocol`) | Handshake failed; the pipe closes after |
| `result` | `request_id`, `ok`, `error?` | Reply to a command |
| `state_changed` | `state` | Capture state changed; sent to every connected client |
| `diagnostics` | see below | Answers `get_diagnostics` |
| `sessions` | `sessions[]` | Answers `list_sessions` |

**Every event may carry a `request_id`**, and one does whenever it answers a command. The client completes
the pending request whose id matches, whatever the event's type; only `state_changed` is ever volunteered.
Before ST-085 the client completed a pending request on a `result` alone, so `capabilities` — which has had
a reply type since ST-021 — would have hung the first time anything asked.

A question is answered with its own event **and then** a `result`, so a client that understands only
results still learns the command was accepted.

`state` is a `CaptureStateSnapshot`: `state` (`idle`, `recording`, `paused`, `suppressed`, `finalizing`, `draft_ready`, `draft_failed`), `session_id?`, `elapsed_ms?` (active time, excluding pauses), `remote_tool?`, `pending_redactions`, `drafts_ready`, `draft_failure_reason?` (only in `draft_failed`). ST-020's `SessionMachine` owns the transitions.


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
## Authentication

Details in ADR-0003. In short: the pipe admits only the same user; the service checks the client executable (signed by the same publisher, or same directory for unsigned dev builds); and the client presents the per-service-run token from `%LOCALAPPDATA%\ScreenTail\ipc.token`. Rejections are audit-logged as `ipc_rejected_<reason>`, nothing else.

## Reattach

The service keeps all state. A reconnecting client sends `hello` and gets the state back in `hello_ack`. Budget: 2 s from UI restart to reattached; measured well under 100 ms.


### `get_diagnostics` → `diagnostics`

ST-071's panel, answered by the service because the UI has no store, no hooks and no counters of its own.

```json
{ "type": "diagnostics", "request_id": 7, "scope": "Capturing - ScreenConnect",
  "microphone": "Headset Microphone (Realtek)", "redaction_backlog": 4, "frames_dropped": 0,
  "keystrokes_dropped": 17, "egress_blocked": 0, "local_only": true, "policy_version": "local",
  "cpu_percent": 3.4, "working_set_bytes": 251658240, "service_version": "0.1.0" }
```

Every field is a state, a count or a device name. `scope` is the scope decision's own words — a process
name and what is being done about it — never a window title, because this text goes on a clipboard and
into tickets (INV-10).

### `list_sessions` → `sessions`

```json
{ "type": "sessions", "request_id": 8, "sessions": [
  { "id": "s1", "started_at": "2026-09-16T09:00:00Z", "duration_ms": 61000, "status": "draft",
    "tool": "screenconnect", "frames": 7, "frames_purged": 1 } ] }
```

`status` is one of `draft`, `published`, `discarded`, `partial`, `pending`, computed by the service so
that the draft's text never crosses the pipe just to be turned back into a single word. `frames` counts
redacted frames only: a pending frame is not one anything outside the redaction worker may count (INV-1).

## Connection limits

The handshake table holds 16 in flight and a peer has two seconds to say `hello`. When the table is full
the **oldest silent** handshake is dropped to make room, and the newcomer is never refused. Refusing the
newcomer was the earlier policy and it handed a same-user process a better attack than the one it fixed:
sixteen silent connections kept the table full, every refusal fell on whoever asked next, and the real UI
was the one turned away — unable to see or change capture state, which defeats INV-4 by making the
indicator unreachable rather than by touching capture.
