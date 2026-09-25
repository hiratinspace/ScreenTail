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
| `get_integrations` | — | What the tenant has connected, from the backend. Answered by `integrations` |
| `search_tickets` | `query` | The ticket picker's search: three characters or a number. Answered by `tickets` |
| `publish_session` | `session_id`, `ticket_id`, `ticket_company?`, `note_type`, `minutes`, `billable`, `destinations[]`, `note`, `frame_ids[]` | Publish (INV-3). The service reads the frames' bytes from the store and asks the backend; the company is the PSA's name for the ticket's, for the knowledge-base mapping. Answered by `published` |
| `get_company_mappings` | — | The documentation platform's companies and what this tenant has mapped (ST-097). Answered by `company_mappings` |
| `map_company` | `psa_company`, `doc_company_id` | The mapping prompt's answer: this PSA company is that platform company. The backend remembers it. Answered by a `result` |
| `get_session` | `session_id` | A session to review. Answered by `session` |
| `get_frame` | `frame_id` | One redacted frame's image. Answered by `frame` |
| `set_frame_included` | `frame_id`, `included` | Space in the filmstrip: whether the frame goes out with the note |
| `delete_frame` | `frame_id` | Delete, once the five-second undo has closed. Fails when it was already gone |
| `blur_frame` | `frame_id`, `x`, `y`, `width`, `height` | Blur a rectangle, in the frame's own pixels. Answered by `frame` with the image as it now is |
| `save_draft` | `session_id`, `draft` | The edited note, written back. The whole note, every time |

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
| `session` | `session` | Answers `get_session`: a `session.v1` document, redacted frames only, with its draft |
| `frame` | `frame_id`, `image` | Answers `get_frame` and `blur_frame`: one image, base64 |
| `integrations` | `integrations[]` (`provider`, `site_url`, `secret` as last four) | Answers `get_integrations` |
| `tickets` | `tickets[]` (`id`, `summary`, `company`, `status`) | Answers `search_tickets` |
| `published` | `results[]` (`destination`, `ok`, `id`, `link`, `error`, `kind`, `retryable`) | Answers `publish_session`, each destination on its own, so Retry sends only what failed. `kind: needs_mapping` on `kb_article` is the one the pane acts on (below) |
| `company_mappings` | `companies[]` (`id`, `name`), `mappings[]` (`psa_company`, `doc_company_id`, `doc_company_name`, `confidence`) | Answers `get_company_mappings` |

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

### Review over the pipe: `get_session`, `get_frame`, the edits

ST-085's remainder (2026-09-25). The UI has no store, and until these existed the Review views were fed
only by the screenshot harness: the shell's Review area showed the word "Review". Everything a technician
does to a session in Review is a question or a command here, and the service — the one process that
reads a frame — does the work.

```json
{ "type": "get_session", "request_id": 11, "session_id": "s1" }
{ "type": "session", "request_id": 11, "session": { "schema_version": "session.v1", "session_id": "s1", "frames": [ … ], "draft": { … } } }
{ "type": "get_frame", "request_id": 12, "frame_id": "f1" }
{ "type": "frame", "request_id": 12, "frame_id": "f1", "image": "/9j/4AAQ…" }
{ "type": "blur_frame", "request_id": 13, "frame_id": "f1", "x": 10, "y": 20, "width": 300, "height": 40 }
{ "type": "frame", "request_id": 13, "frame_id": "f1", "image": "/9j/4AAQ…" }
```

`session` is the same `session.v1` shape the fixtures and the bundle use, as the store hands it out:
**redacted frames only** (INV-1), so a pending frame is absent rather than flagged. `get_frame` for a
pending frame is refused, not answered. One image per message, because the pipe's frame limit is 1 MiB
and a redacted screenshot is a few hundred kilobytes; a frame over 700 KiB is refused with its size in
the reason rather than closing the connection.

`blur_frame` is painted by the service with the same masker redaction uses, written to the store, and
only then answered — so a crash between the write and the reply leaves the disk ahead of the screen,
never behind it. The region is recorded as `user_blur` in the frame's `masked_regions`. Destructive, and
meant to be (Spec §5 S3). The UI used to flatten in WPF and hand the bytes back; that re-encoded a JPEG
as PNG on the UI thread and made the stored name a lie (weaknesses P2-9).

None of this bumps the version: an older client never sends these and never sees their replies.

### Publishing: `get_integrations`, `search_tickets`, `publish_session`

ST-093 (2026-09-25). The UI never talks to the backend and never learns which PSA the tenant runs: it
asks the service, which holds the device token and the egress guard, and the service asks the backend,
which holds the tenant's credential in its vault. The backend's per-destination answer comes back as it
is. A refusal — no PSA connected, a revoked device, a backend that did not answer — arrives as a failed
`result` carrying the backend's own words, which the pane shows beside the button.

```json
{ "type": "publish_session", "request_id": 21, "session_id": "s1", "ticket_id": "48213", "note_type": "internal",
  "minutes": 30, "billable": true, "destinations": ["ticket_note", "time_entry"], "note": { … }, "frame_ids": ["f1", "f3"] }
{ "type": "published", "request_id": 21, "results": [
  { "destination": "ticket_note", "ok": true, "id": "90001", "link": null, "error": null, "kind": null, "retryable": false },
  { "destination": "time_entry", "ok": false, "id": null, "link": null, "error": "ConnectWise says this API member may not do that. Ask your ConnectWise administrator for the permission.", "kind": "forbidden", "retryable": false } ] }
```

The frames are named, not sent: the service reads each one's bytes from the store, and only a redacted
frame has any (INV-1). These three go out under the egress guard's user-initiated purpose, which
local-only mode does not stop (INV-8): the technician pressed Publish and knows where it is going.

### The company mapping: `get_company_mappings`, `map_company`

ST-097 (2026-09-25). A knowledge-base article is filed under the ticket's company in the documentation
platform, and the backend keys that by the PSA's name for the company. When it has no mapping, the
`kb_article` result comes back `ok: false, kind: "needs_mapping"` with the backend's sentence, and the
pane asks once, at publish: it lists the platform's companies with `get_company_mappings`, sends the
choice with `map_company`, and publishes the article again on its own. Whatever landed stays landed.
Both calls are the service asking `GET` and `PUT /v1/integrations/hudu/companies` on the pane's behalf,
under the same purpose as the publish.

```json
{ "type": "get_company_mappings", "request_id": 22 }
{ "type": "company_mappings", "request_id": 22,
  "companies": [ { "id": "7", "name": "Acme Dental Group" }, { "id": "9", "name": "Bright Smiles" } ],
  "mappings": [ { "psa_company": "Bright Smiles", "doc_company_id": "9", "doc_company_name": "Bright Smiles", "confidence": "exact" } ] }
{ "type": "map_company", "request_id": 23, "psa_company": "Acme Dental", "doc_company_id": "7" }
{ "type": "result", "request_id": 23, "ok": true }
```

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
| 2026-09-25 | `get_session` / `session`, `get_frame` / `frame`, `set_frame_included`, `delete_frame`, `blur_frame`, `save_draft` (ST-085 remainder) | 2 |
| 2026-09-25 | `get_integrations` / `integrations`, `search_tickets` / `tickets`, `publish_session` / `published` (ST-093) | 2 |
| 2026-09-25 | `session` may carry `suggested_ticket` (schema session.v1; ST-077): digits read off the window at start, never the title | 2 |
| 2026-09-25 | `get_company_mappings` / `company_mappings`, `map_company` (ST-097 client half) | 2 |
