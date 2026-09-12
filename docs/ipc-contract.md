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

## Events (service → UI)

| `type` | Fields | When |
|---|---|---|
| `hello_ack` | `contract_version`, `service_version`, `state` | Handshake succeeded |
| `rejected` | `reason` (`unverified_client`, `bad_token`, `contract_mismatch`, `protocol`) | Handshake failed; the pipe closes after |
| `result` | `request_id`, `ok`, `error?` | Reply to a command |
| `state_changed` | `state` | Capture state changed; sent to every connected client |

`state` is a `CaptureStateSnapshot`: `state` (`idle`, `recording`, `paused`, `suppressed`, `finalizing`, `draft_ready`, `draft_failed`), `session_id?`, `elapsed_ms?` (active time, excluding pauses), `remote_tool?`, `pending_redactions`, `drafts_ready`, `draft_failure_reason?` (only in `draft_failed`). ST-020's `SessionMachine` owns the transitions.

## Authentication

Details in ADR-0003. In short: the pipe admits only the same user; the service checks the client executable (signed by the same publisher, or same directory for unsigned dev builds); and the client presents the per-service-run token from `%LOCALAPPDATA%\ScreenTail\ipc.token`. Rejections are audit-logged as `ipc_rejected_<reason>`, nothing else.

## Reattach

The service keeps all state. A reconnecting client sends `hello` and gets the state back in `hello_ack`. Budget: 2 s from UI restart to reattached; measured well under 100 ms.
