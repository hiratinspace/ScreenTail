# Service–UI IPC contract (v4)

The capture service and the UI talk over a named pipe (`ScreenTail.<user-sid>`; on macOS/Linux dev boxes a Unix socket of the same name). Design and security rationale: [ADR-0003](adr/0003-process-hosting-and-ipc.md). Types: `client/ScreenTail.Shared/Ipc/`. Transport: `client/ScreenTail.Core/Ipc/`.

**Last brought into line with the code: 2026-09-28.** `IpcContract.Version` is **4**, since 2026-10-23 (2026-10-12 review, E-2): the `audit` reply carries where the export was written and its verdict instead of the whole log as one string, and an older reader would have failed on the new fields. Version 3 (2026-10-14, ST-081) added `effective_local_only` and `effective_retention_days` to `settings`, so Privacy shows a locked field as the admin set it. Version 2 (2026-09-14, ST-072, #46) added `reason` and `scope_process` to the state snapshot. All three bumped for the same reason: the framing rejects unknown members, so an older reader would fail on a changed reply rather than ignore it. The v3 change was checked against the code for the `get_settings` and `settings` rows only, and the v4 change for the `export_audit` and `audit` rows only. Everything else since has been new commands and events an older client never sends or receives, and has not bumped it.

## Framing

Each message is one frame: a 4-byte little-endian unsigned length, then that many bytes of UTF-8 JSON. Maximum 1 MiB; a larger length closes the connection. No message is ever split across frames or batched. The service never sends a larger answer: one that would not fit is refused before a byte is written, with a failed `result` that gives its size, and the connection stays up.

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
| `start` | — | Start a session now. Sent only by the onboarding wizard's test session; Ctrl+Alt+R is a hotkey the service registers and handles itself, and the tray has no start item. Refused while no `indicator_showing` report is current, as a start the service makes by itself is (INV-4), with a `result` error saying why and what to do. Starts under the tenant's policy as it stands now: the same local-only flag and policy version as every other start (ST-047) |
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
| `search_tickets` | `query` | The ticket picker's search: three characters or a number. Empty, it asks for the recent tickets the picker shows on focus (Spec §5 S3). Answered by `tickets` |
| `publish_session` | `session_id`, `ticket_id`, `ticket_company?`, `note_type`, `minutes`, `billable`, `destinations[]`, `note`, `frame_ids[]` | Publish (INV-3). The service reads the frames' bytes from the store and asks the backend. Only frames of that session that the drafting bundle may use are attached (redacted, not `sensitive_context`, not excluded in Review, not taken while capture was off), each once; any other id named is dropped (ST-078); the company is the PSA's name for the ticket's, for the knowledge-base mapping. Answered by `published` |
| `get_company_mappings` | — | The documentation platform's companies and what this tenant has mapped (ST-097). Answered by `company_mappings` |
| `map_company` | `psa_company`, `doc_company_id` | The mapping prompt's answer: this PSA company is that platform company. The backend remembers it. Answered by a `result` |
| `activate_device` | `code`, `device_name` | The invite's code and this machine's name (ST-010, Spec §5 S8 step 2). The service keeps the refresh token the backend hands back under DPAPI; the UI only ever sees the tenant's name. Answered by `device_activated`, or a failed `result` with the backend's sentence |
| `get_device` | — | The device's standing. Answered by `device` |
| `get_settings` | — | The technician's own settings, which fields the admin's policy locked, and the values in force (ST-081). Answered by `settings` |
| `set_settings` | `settings` | The whole document, every time; the service validates, writes it beside the store and applies it in place. Refused with the first problem as a failed `result` |
| `export_audit` | — | Writes the audit log with its chain verification, as JSON, to a new file in the service's `exports` folder under its data directory, verified in the same pass (Spec §5 S6 "Export audit log"). Answered by `audit`, or a failed `result` saying why. The log itself never crosses the pipe, and the service never takes a path from it |
| `get_capture_settings` | — | The capture settings with the registry's tools, the device found and what the policy forces beside them (ST-080). Answered by `capture_settings` |
| `set_capture_settings` | `settings` | The whole document, every time; validated, written beside the store, applied in place (hotkeys re-registered). Refused with the first problem as a failed `result` |
| `list_integrations` | — | Settings → Integrations' list: each provider with its site, the credential's last four, when it was connected and last checked, and the last error (ST-082). Answered by `integration_details` |
| `store_integration` | `provider`, `site_url`, `secret` | Stores or replaces a credential. The one message a secret ever travels in; it is never read back. Answered by a `result` |
| `remove_integration` | `provider` | Forgets a credential. Answered by a `result` |
| `check_integration` | `provider` | "Test connection". Answered by `integration_checked` either way; a refusal (no backend, not enrolled) is a failed `result` |
| `unmap_company` | `psa_company` | Forgets a company mapping. Answered by a `result` |
| `get_session` | `session_id` | A session to review. Answered by `session` |
| `get_frame` | `frame_id` | One redacted frame's image. Answered by `frame` |
| `get_frame_thumbnail` | `frame_id` | One redacted frame's image scaled to a 320 px long edge for the filmstrip. Answered by `frame`; nothing is painted or written. The full image stays `get_frame`'s, for the enlarged view |
| `get_frame_text` | `frame_id` | One redacted frame's OCR text, for `T` in the enlarged view. Answered by `frame_text` |
| `set_frame_included` | `frame_id`, `included` | Space in the filmstrip: whether the frame goes out with the note. Fails when there is no such frame, and the UI then shows an inclusion that failed as excluded again |
| `delete_frame` | `frame_id` | Delete, once the five-second undo has closed. Fails when it was already gone. Del itself sends `set_frame_included` with `included: false` at once for a frame that was going out, and Undo sends it back, so a row that outlives the window because the UI or the pipe went does not go out |
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
| `session` | `session` | Answers `get_session`: a `session.v1` document, redacted frames only, with its draft and without any frame's `ocr_text`. Refused with its size when it would not fit in one message |
| `frame` | `frame_id`, `image` | Answers `get_frame`, `get_frame_thumbnail` and `blur_frame`: one image, base64 |
| `frame_text` | `frame_id`, `ocr_text?` | Answers `get_frame_text`. `ocr_text` is absent when the frame had none |
| `integrations` | `integrations[]` (`provider`, `site_url`, `secret` as last four) | Answers `get_integrations` |
| `tickets` | `tickets[]` (`id`, `summary`, `company`, `status`) | Answers `search_tickets` |
| `published` | `results[]` (`destination`, `ok`, `id`, `link`, `error`, `kind`, `retryable`) | Answers `publish_session`, each destination on its own, so Retry sends only what failed. `kind: needs_mapping` on `kb_article` is the one the pane acts on (below). `kind: unknown` means it may have been saved; its `error` says to check before publishing again, and `retryable` is false |
| `company_mappings` | `companies[]` (`id`, `name`), `mappings[]` (`psa_company`, `doc_company_id`, `doc_company_name`, `confidence`) | Answers `get_company_mappings` |
| `device_activated` | `tenant_name`, `device_id` | Answers `activate_device` |
| `device` | `activated`, `tenant_name`, `device_id`, `days_offline`, `beyond_grace`, `revoked`, `standing` | Answers `get_device`: one sentence in `standing` for a screen |
| `settings` | `settings` (`local_only`, `telemetry`, `retention_days`, `ssn`, `cards`, `api_keys`, `passwords`, `emails`, `custom_patterns[]`, `excluded_processes[]`), `local_only_locked`, `retention_locked`, `policy_version`, `effective_local_only`, `effective_retention_days` | Answers `get_settings`. `settings` is the technician's own document, the one `set_settings` sends back. The `effective_` pair is what is enforced: the policy's value for a locked field (retention up to 365 days), otherwise the technician's own. Privacy shows the effective value for a locked field, so "Set by your admin" never sits beside the technician's value |
| `audit` | `path`, `rows`, `intact`, `note` | Answers `export_audit`: where the file is, how many rows it holds, and the verification. The UI copies the file where the technician chose and deletes it; one left behind is removed by the next export after ten minutes, and by `erase_all_local_data` |
| `capture_settings` | `settings` (`disabled_tools[]`, `capture_all_windows`, `auto_start`, `grace_seconds`, `hotkeys{}`, `sensitivity`, `speech_model`, `start_ui_at_login`), `tools[]` (`id`, `display_name`), `microphones[]`, `policy_forces_all_windows`, `default_grace_seconds` | Answers `get_capture_settings` |
| `integration_details` | `integrations[]` (`provider`, `site_url`, `secret` as last four, `connected_at`, `last_checked_at`, `last_error`) | Answers `list_integrations` |
| `integration_checked` | `ok`, `message` | Answers `check_integration`: the provider's answer in words, either way |

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
| `scope_process?` | The process in front when capture is out of scope, so the HUD can say "Not capturing: Outlook". A process name, never a window title (INV-10) |
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
is the depth of the in-memory queue (ADR-0006); `frames_dropped` counts frames lost unchecked (INV-1),
whether deleted because they could not be read or refused because that queue was full. `local_only` and `egress_blocked` come from the same policy object the
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
coordinates, so the claim is specific enough to check against a screenshot, or, with a zero-size
rectangle, that the technician chose tray-only between sessions (below). It is sent while it is true
and not once at startup, because the interesting failure is a UI that stops painting without closing: the
socket stays open and the pill goes stale. A report expires after six seconds, and a connection is not
an indicator — the service counts pills it has heard from, not clients it can see (INV-4, #130). While
none is reported, `IndicatorGuard` suppresses capture after a short grace for UI restarts.

A report with a zero width and height means the technician chose tray-only between sessions, and the
pill returns as a session starts (owner decision, 2026-10-25). INV-4 allows tray-only as the technician's
choice, and the report keeps a session the service starts by itself from being refused for want of the
pill they put away. A session that starts must be followed by a real pill report: if the pill fails to
come back, or comes back on no monitor, the zero-size report expires and `IndicatorGuard` suppresses after
its grace, so capture runs without a pill for at most one report's lifetime (six seconds) plus that grace.
A pill that is up but on no monitor is not reported at all. The wire format is unchanged and the version
is not bumped.

The same reports gate `start`: with none current the service answers a failed `result` and starts
nothing, as it does for a session it would start by itself. A zero-size report counts, as above.

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
{ "type": "get_frame_text", "request_id": 14, "frame_id": "f1" }
{ "type": "frame_text", "request_id": 14, "frame_id": "f1", "ocr_text": "Print Spooler  Stopped  Automatic" }
```

`session` is the same `session.v1` shape the fixtures and the bundle use, as the store hands it out:
**redacted frames only** (INV-1), so a pending frame is absent rather than flagged. `get_frame` for a
pending frame is refused, not answered. One image per message, because the pipe's frame limit is 1 MiB
and a redacted screenshot is a few hundred kilobytes; a frame over 700 KiB is refused with its size in
the reason rather than closing the connection.

`session` leaves every frame's `ocr_text` out (E-20, 2026-10-14). On a long, text-heavy session the
text was most of the message: about two hours of busy capture passed 1 MiB, the write threw, the
service dropped the UI's connection, and the session could never be opened in Review. The pane shows
the text for one enlarged frame at a time, so it asks `get_frame_text` when a frame is opened and lets
the text go when the frame closes or another one opens. The store still holds it, and the drafter and the bundle read it
there. What remains (frames, events, transcript, draft) is measured on the service before it is
written, in the bytes the framing would write; a session that still would not fit is refused with a
failed `result` that gives its size and the limit, and the connection stays up. `frame_text` is
measured the same way. Like `get_frame`, a pending frame's text is refused, not answered (INV-1).

`blur_frame` is painted by the service with the same masker redaction uses, written to the store, and
only then answered — so a crash between the write and the reply leaves the disk ahead of the screen,
never behind it. The region is recorded as `user_blur` in the frame's `masked_regions`. Destructive, and
meant to be (Spec §5 S3). The rectangle is in the frame's own pixels (its `width` and `height`, which
every masked region uses) and must lie wholly inside them, or it is refused and nothing is painted. The
stored image is smaller, because redaction downscales it to a 1600 px long edge, so the service scales
the rectangle into the stored image's pixels and rounds outward before painting: every stored pixel
that shows any part of it is covered, plus one stored pixel on each side of a downscaled axis, because
the bilinear downscale blends a little of the pixels just inside the box into the ones just outside it.
If the image the masker decodes is not the size the store recorded, the blur is refused rather than
painted somewhere else. The frame's `ocr_text` is cleared in the same write, because it was read from
the pixels just destroyed and nothing records which words were under the rectangle (ST-075). A blur
that is written but whose new image is over the 700 KiB limit gets no `frame` reply; its result says it
was written and that the session has to be opened again to show it. A refused blur's result gives the
reason it stopped for. The UI used to flatten in WPF and hand the bytes back; that re-encoded a JPEG
as PNG on the UI thread and made the stored name a lie (weaknesses P2-9).

None of this bumps the version: an older client never sends these and never sees their replies.

### Publishing: `get_integrations`, `search_tickets`, `publish_session`

ST-093 (2026-09-25). The UI never talks to the backend and never learns which PSA the tenant runs: it
asks the service, which holds the device token and the egress guard, and the service asks the backend,
which holds the tenant's credential in its vault. The backend's per-destination answer comes back as it
is. A refusal — no PSA connected, a revoked device, a backend that did not answer — arrives as a failed
`result` carrying the backend's own words, which the pane shows beside the button. The UI reads the
reason off that one reply. It never sends `publish_session` again to learn it, because a second send is a
second publish; `activate_device` and `check_integration` are read the same way (2026-10-14, S-36).

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

## Answering order

E-25, 2026-10-12 review. A connection's commands are answered on two lanes, so replies can arrive in a different order from the commands, and the client matches them by `request_id`.

- The session's own commands are answered as soon as they are read, in the order they arrive: `indicator_showing`, `start`, `pause`, `resume`, `stop`, `discard`, `mark_moment`, `request_confirmation`, `get_state`, `get_capabilities`. They are answered from local state and never wait behind anything on the network.
- So are the two settings writes, `set_settings` and `set_capture_settings`, which are a local file and an in-memory apply. A session command sent after a setting change on the same connection (the tray and the hotkey send Start without waiting) is therefore always handled after that change, never under the settings it replaced.
- Every other command, including any added later, goes to one queue per connection and is run one at a time in the order it was sent, as every command was before. At most 16 wait behind the one running; one more is answered at once with `ok: false` and a "busy" sentence rather than read later. When the client disconnects, whatever is still waiting is dropped, not run.
- If a command on either lane fails, the service closes the connection at once, even while a queued command is still running, so the client sees the pipe end and reconnects. The running command finishes, but its reply goes nowhere.
- An answer too large for one frame is not such a failure, on either lane: it is refused before a byte is written, with a failed `result` that gives its size, and the connection stays up (E-2). `get_session`, `get_frame`, `get_frame_thumbnail` and `get_frame_text`, the replies most likely to be large, all queue.

Before this a slow backend call (a publish, a ticket search) held up the HUD's `indicator_showing` past the indicator's grace, and the service dropped frames and typing as unindicated with the pill on screen (INV-4, INV-6). The wire format is unchanged and the version is not bumped: the client has always matched replies by `request_id`.

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
| 2026-09-25 | `activate_device` / `device_activated`, `get_device` / `device` (ST-010 client half) | 2 |
| 2026-09-26 | `get_settings` / `settings`, `set_settings`, `export_audit` / `audit` (ST-081) | 2 |
| 2026-09-26 | `get_capture_settings` / `capture_settings`, `set_capture_settings` (ST-080) | 2 |
| 2026-09-25 | `list_integrations` / `integration_details`, `store_integration`, `remove_integration`, `check_integration` / `integration_checked`, `unmap_company` (ST-082) | 2 |
| 2026-10-14 | `effective_local_only`, `effective_retention_days` on `settings` (ST-081) | **3** |
| 2026-10-14 | `get_frame_text` / `frame_text`; `session` leaves out every frame's `ocr_text` and is refused with its size when it would not fit (E-20). An older client reads the session as before and shows no OCR text | 3 |
| 2026-10-23 | `audit` carries `path`, `rows`, `intact`, `note` instead of `json`; an answer too large for one frame is a failed `result` (2026-10-12 review, E-2) | **4** |
| 2026-10-14 | Two answering lanes per connection: the session's commands and the two settings writes are answered on the read loop, the rest queue (16 at most) behind one worker (E-25) | 4 |
| 2026-10-25 | `get_frame_thumbnail`, answered by `frame`: the filmstrip draws from a thumbnail the service scales instead of every full redacted image (E-30). A new command, so the version is not bumped | 4 |
