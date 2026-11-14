# Service–UI IPC contract (v11)

The capture service and the UI talk over a named pipe (`ScreenTail.<user-sid>`; on macOS/Linux dev boxes a Unix socket of the same name). Design and security rationale: [ADR-0003](adr/0003-process-hosting-and-ipc.md). Types: `client/ScreenTail.Shared/Ipc/`. Transport: `client/ScreenTail.Core/Ipc/`.

**Last brought into line with the code: 2026-11-12.** `IpcContract.Version` is **11**, since 2026-11-12 (spec v0.6.9 S4, ST-016): a `sessions` row gains `title`, `ticket` and `company`, so the inbox rail can say the company, the note's title and the ticket. Each is optional and left off when unknown, so a row without them is the v10 row and is drawn as before. The change is additive, but the framing rejects unknown members, so a v10 UI would fail reading a v11 row that has them: the version moves and the handshake turns a mismatched pair away rather than leaving the rail unreadable. It was **10** since 2026-11-10 (ST-093): a `sessions` row gains `publish_unconfirmed` and the state snapshot `publishes_unconfirmed`, so History and the tray can say a publish was not confirmed without Review being opened. An older UI would fail on either new member. `mark_published`, which settles such a session on the technician's word, is a new command and would not have moved it on its own. It was **9** since 2026-11-08 (ST-093): `publish_session` gains an optional `publish_again`, and without it the service refuses any destination it holds as landed or unknown, so a lost answer, a restarted UI or a second window cannot put the note on the ticket twice. An older service would refuse the new member. It was **8** since 2026-11-04 (ST-031): the `diagnostics` reply gains `name_hook_refusals`, how many times Windows refused the foreground watcher's narrow title-change hook, so the panel can show it. An older UI would fail on the new member. It was **7** since 2026-10-31 (ST-080 AC3): `capture_settings` lists `microphones[]` as rows with an id, a name and the default marked, where it listed names, and the capture settings document gains `microphone_id`. An older reader would fail on the changed reply and an older service would refuse the new member. The level meter's `get_microphone_level`, `stop_microphone_level` and `microphone_level` are new messages and would not have moved it on their own. It was **6** since 2026-10-31 (ST-074, ST-079): `request_confirmation` gains `session_ids`, so Review's Discard and History's bulk discard name their sessions and the token is bound to exactly those, and `discard_sessions` carries them back. An older service would refuse the new field as an unknown member. It was **5** since 2026-10-29 (ST-013): the state snapshot has a new state, `draft_pending`, and the `session` reply a `draft_pending` flag, so Review and the pill say a queued draft is on its way instead of failed. It was **4** since 2026-10-23 (2026-10-12 review, E-2): the `audit` reply carries where the export was written and its verdict instead of the whole log as one string, and an older reader would have failed on the new fields. Version 3 (2026-10-14, ST-081) added `effective_local_only` and `effective_retention_days` to `settings`, so Privacy shows a locked field as the admin set it. Version 2 (2026-09-14, ST-072, #46) added `reason` and `scope_process` to the state snapshot. All of them bumped for the same reason: the framing rejects unknown members, so an older reader would fail on a changed reply rather than ignore it. The v3 change was checked against the code for the `get_settings` and `settings` rows only, and the v4 change for the `export_audit` and `audit` rows only. Everything else since has been new commands and events an older client never sends or receives, and has not bumped it.

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
| `start` | none | Start a session now. Sent only by the onboarding wizard's test session; Ctrl+Alt+R is a hotkey the service registers and handles itself, and the tray has no start item. Refused while no `indicator_showing` report is current, as a start the service makes by itself is (INV-4), with a `result` error saying why and what to do. Starts under the tenant's policy as it stands now: the same local-only flag and policy version as every other start (ST-047) |
| `pause` | none | Pause capture |
| `resume` | none | Resume capture |
| `stop` | none | Stop and draft |
| `discard` | `confirmation` | The tray's Discard: stop and delete the current session and its raw data, whatever it is. Refused without a token issued for `discard_session` with no `session_ids` (below). Unchanged by v6 |
| `discard_sessions` | `session_ids[]`, `confirmation` | Review's Discard (one id) and History's bulk discard (several), v6. Refused, and the token spent, unless the token was issued on this connection for exactly these ids, in any order. All or nothing: an unknown, already discarded or published session refuses the whole set with a sentence saying how many and why, and nothing is discarded. Otherwise, under the machine's gate and in one transaction, each session is marked `discarded`: its note, its suggested ticket and its queued outbox work go, an audit row records how many screenshots it held (no content), and from that commit nothing reads its screenshots, transcript or events and no late write adds to them. The content is then deleted outside the gate in chunks of 50, a transaction each, before the result is sent (and at service start and on the hourly retention tick for a discard left unfinished; a delete that fails is logged by kind and tried again there, and the discard still answers `ok: true`, because the sessions are discarded once marked), so the password and login guards' suppression never waits behind it. A published session is one whose publish landed at least one destination; it is kept. The machine's current session, when named, goes the way `discard` takes it: a recording one stops capturing first, the row is deleted and the state moves to `idle`. Queued behind the connection's worker, not on the read loop (below) |
| `mark_moment` | none | Force a frame and a marker event |
| `get_state` | none | Ask for the current state (also arrives in `hello_ack`) |
| `get_capabilities` | none | Re-run the Windows checks (ST-021). Answered by `capabilities` |
| `get_diagnostics` | none | What is being captured right now (ST-071). Answered by `diagnostics` |
| `list_sessions` | `limit?` (default 200) | The History screen's rows (ST-079). Answered by `sessions` |
| `request_confirmation` | `action` (`discard_session`, `discard_sessions` or `erase_everything`), `session_ids?` (v6) | Ask for a one-use token before something irreversible (ST-085). Without `session_ids`, `discard_session` is the tray's and `erase_everything` is erase. With them, `discard_session` names one session and the phrase is `DISCARD` (Review), and `discard_sessions` names one or more and the phrase is how many (History, v0.4.1 Q6); the token is good only on a `discard_sessions` naming exactly those ids. No ids, a blank or doubled id, more than 1000, or ids on erase get no token. Answered by `confirmation` |
| `erase_all_local_data` | `confirmation` | Delete everything and stop (INV-12). Refused without a token issued for `erase_everything`. The pipe drops straight after: the store it was serving is being deleted |
| `indicator_showing` | `x`, `y`, `width`, `height` | The UI saying the recording pill is on screen, and where (INV-4). Sent every two seconds while it is true; the service forgets a report it has not heard repeated within six seconds |
| `get_integrations` | none | What the tenant has connected, from the backend. Answered by `integrations` |
| `search_tickets` | `query` | The ticket picker's search: three characters or a number. Empty, it asks for the recent tickets the picker shows on focus (Spec §5 S3). Answered by `tickets` |
| `publish_session` | `session_id`, `ticket_id`, `ticket_company?`, `note_type`, `minutes`, `billable`, `destinations[]`, `note`, `frame_ids[]`, `publish_again?` | Publish (INV-3). The service reads the frames' bytes from the store and asks the backend. Only frames of that session that the drafting bundle may use are attached (redacted, not `sensitive_context`, not excluded in Review, not taken while capture was off), each once; any other id named is dropped (ST-078); the company is the PSA's name for the ticket's, for the knowledge-base mapping. Without `publish_again` (v9), refused when any destination named is held as landed or unknown by an earlier publish, or for any destination of a session published before that record existed; refused for a session whose earlier publish has not answered yet whatever it says, and for a destination name it does not know (below). Answered by `published` |
| `mark_published` | `session_id` | History's "Mark as published" (v10): the technician checked the ticket and what an earlier publish may have left is there. Every destination the service holds as `unknown` for the session becomes `landed`, the unconfirmed mark is cleared, and the session is marked published by the write a landing makes. Nothing is sent to the ticket or the PSA. With telemetry on, a session nothing had landed for queues its session metric (counts and times, `published: true`) through the outbox, as a landing does; with telemetry off nothing leaves the machine. Refused, with a sentence, for a session with no destination held as `unknown` (never published, published cleanly, already settled, or published before outcomes were kept), for a discarded session (INV-12), while a publish of it has not answered yet, and for a session that is gone (below). Answered by a `result` |
| `get_company_mappings` | none | The documentation platform's companies and what this tenant has mapped (ST-097). Answered by `company_mappings` |
| `map_company` | `psa_company`, `doc_company_id` | The mapping prompt's answer: this PSA company is that platform company. The backend remembers it. Answered by a `result` |
| `activate_device` | `code`, `device_name` | The invite's code and this machine's name (ST-010, Spec §5 S8 step 2). The service keeps the refresh token the backend hands back under DPAPI; the UI only ever sees the tenant's name. Answered by `device_activated`, or a failed `result` with the backend's sentence |
| `get_device` | none | The device's standing. Answered by `device` |
| `get_settings` | none | The technician's own settings, which fields the admin's policy locked, and the values in force (ST-081). Answered by `settings` |
| `set_settings` | `settings` | The whole document, every time; the service validates, writes it beside the store and applies it in place. Refused with the first problem as a failed `result` |
| `export_audit` | none | Writes the audit log with its chain verification, as JSON, to a new file in the service's `exports` folder under its data directory, verified in the same pass (Spec §5 S6 "Export audit log"). Answered by `audit`, or a failed `result` saying why. The log itself never crosses the pipe, and the service never takes a path from it |
| `get_capture_settings` | none | The capture settings with the registry's tools, the capture devices connected now and what the policy forces beside them (ST-080). Answered by `capture_settings` |
| `set_capture_settings` | `settings` | The whole document, every time; validated, written beside the store, applied in place (hotkeys re-registered; the microphone used the next time it opens). Refused with the first problem as a failed `result` |
| `get_microphone_level` | `device_id?` | Settings, Capture's live level meter (ST-080 AC3, v7). The device the picker shows, saved or not; absent is Windows' default, and one not connected falls back to it. Sent about ten times a second while the card is on screen and scrolled into view, with the window in front and the desktop unlocked and awake; each after the last was answered and after a round trip through the UI's dispatcher, so a hung window sends none. Each one is a short lease (1.5 s): the first opens the device, each one after keeps it open, and the service closes it by itself within a few seconds of the last. The device is never open for more than five minutes at a stretch: the meter then answers `timed_out` without opening anything until a `stop_microphone_level`, which the card sends when it is put away; a session starting and ending in between does not reset it. Refused without opening anything, as `state: session`, while a session is recording, paused, suppressed or finishing; a session that starts closes it. Opened as plain capture, not in communications mode, so Windows does not turn other sound down. Levels only: no audio crosses the pipe and nothing heard is kept, transcribed, sent or logged. Answered by `microphone_level` |
| `stop_microphone_level` | (none) | The card was put away: the meter closes the device now rather than when its lease runs out, and one that reached its five minutes may open again. Always `ok` |
| `list_integrations` | none | Settings → Integrations' list: each provider with its site, the credential's last four, when it was connected and last checked, and the last error (ST-082). Answered by `integration_details` |
| `store_integration` | `provider`, `site_url`, `secret` | Stores or replaces a credential. The one message a secret ever travels in; it is never read back. Answered by a `result` |
| `remove_integration` | `provider` | Forgets a credential. Answered by a `result` |
| `check_integration` | `provider` | "Test connection". Answered by `integration_checked` either way; a refusal (no backend, not enrolled) is a failed `result` |
| `unmap_company` | `psa_company` | Forgets a company mapping. Answered by a `result` |
| `get_update` | (none) | Where this installation stands with updates (ST-113). Answered by `update`. New commands, so the version is not bumped for them |
| `install_update` | (none) | Installs the update that is ready: refused with a failed `result` while a session is recording, when nothing is ready, or when the package fails its checks again just before Windows Installer is started. Until Windows Installer exits no session starts. New commands, so the version is not bumped for them |
| `set_update_channel` | `channel` | `stable` or `pilot`. Refused when the admin's policy chose the channel (INV-11). Answered by a `result`. New commands, so the version is not bumped for them |
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
| `state_changed` | `state` | Capture state changed, or a count it carries (`drafts_ready`, `pending_redactions`) moved; sent to every connected client. Not sent for the clock: `elapsed_ms` is as of the snapshot, and the UI counts on from it locally while recording (ST-072) |
| `capabilities` | `checked_at`, `can_capture`, `checks[]` | Answers `get_capabilities`; also sent when a check changes |
| `diagnostics` | see below | Answers `get_diagnostics` |
| `sessions` | `sessions[]` | Answers `list_sessions` |
| `confirmation` | `token`, `phrase` | Answers `request_confirmation` |
| `session` | `session` | Answers `get_session`: a `session.v1` document, redacted frames only, with its draft and without any frame's `ocr_text`, and `draft_pending` (bool, v5): the session's draft is queued for the backend and has not come back, so Review says it is being drafted rather than failed. `publish_outcomes` (object, v9, absent when nothing was published): destination (`ticket_note`, `time_entry`, `kb_article`) to `landed` or `unknown`, what earlier publishes left on the ticket, for the publish pane to open on; every destination `unknown` for a session published before outcomes were kept. Refused with its size when it would not fit in one message |
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
| `capture_settings` | `settings` (`disabled_tools[]`, `capture_all_windows`, `auto_start`, `grace_seconds`, `hotkeys{}`, `sensitivity`, `speech_model`, `microphone_id` (v7), `start_ui_at_login`), `tools[]` (`id`, `display_name`), `microphones[]` (`id`, `name`, `is_default`, v7; names alone before), `policy_forces_all_windows`, `default_grace_seconds` | Answers `get_capture_settings`. `microphone_id` is Windows' endpoint id for the chosen device, null for the default communications device. A device name crosses the pipe to the same user's Settings and is never logged |
| `microphone_level` | `state` (`closed`, `opening`, `listening`, `session`, `no_microphone`, `unavailable`, `timed_out`), `level` (0 to 1, the peak on a decibel scale from -60 dBFS), `device_id?` (the device listened to) | Answers `get_microphone_level`, v7. A number, never audio |
| `integration_details` | `integrations[]` (`provider`, `site_url`, `secret` as last four, `connected_at`, `last_checked_at`, `last_error`) | Answers `list_integrations` |
| `integration_checked` | `ok`, `message` | Answers `check_integration`: the provider's answer in words, either way |
| `update` | `state`, `installed_version`, `available_version?`, `channel`, `channel_locked`, `sentence`, `can_install` | Answers `get_update`. `state` is one of `not_configured`, `development_build`, `off`, `channel_unknown`, `blocked`, `waiting`, `checking`, `up_to_date`, `available`, `downloading`, `ready`, `installing`, `refused`, `failed`; `sentence` is for a screen and never quotes a manifest; `can_install` is ready and no session recording. Sent only in reply to `get_update`, so the version is not bumped for it |

**Every event may carry a `request_id`**, and one does whenever it answers a command. The client completes
the pending request whose id matches, whatever the event's type; only `state_changed` is ever volunteered.
Before ST-085 the client completed a pending request on a `result` alone, so `capabilities` (which has had
a reply type since ST-021) would have hung the first time anything asked.

A question is answered with its own event **and then** a `result`, so a client that understands only
results still learns the command was accepted.

### The state snapshot

`state` is a `CaptureStateSnapshot`:

| Field | Meaning |
|---|---|
| `state` | `idle`, `recording`, `paused`, `suppressed`, `finalizing`, `draft_pending`, `draft_ready`, `draft_failed`. ST-020's `SessionMachine` owns the transitions. `draft_pending` (v5): the session is over and its draft is queued for the backend; nothing is captured, the session is named and can be discarded, and the outbox moves it on to `draft_ready` or `draft_failed` |
| `session_id?`, `elapsed_ms?` | The session, and its active time excluding pauses |
| `remote_tool?` | Which tool started it |
| `reason?` | Why capture is not running when the state alone does not say: `password_field`, `excluded_app`, `elevated_window`, `sensitive_context`, `out_of_scope`, `user` (ST-072; Spec §5 S2 gives each its own words) |
| `scope_process?` | The process in front when capture is out of scope, so the HUD can say "Not capturing: Outlook". A process name, never a window title (INV-10) |
| `pending_redactions`, `drafts_ready` | Counts |
| `publishes_unconfirmed` | How many sessions hold a publish that may have reached its ticket and was never confirmed (v10). Discarded sessions are not counted. Read wherever `drafts_ready` is read (at start and on the transitions that read it), after any discard that removed a session, whether or not the machine was resting on it, and again whenever a publish record is written. One read at a time, so the count kept is always the newest read. A change is volunteered as a `state_changed` the way `pending_redactions` is, without a transition. The tray says it after the state, between sessions only ("Not capturing · 1 publish not confirmed"), and in its tooltip only when the whole line fits in the 63 characters Windows shows |
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
blocking: "we couldn't tell" must never read as "fine". `can_capture` is false when any of the desktop
session, screen capture or input hooks is blocked; a blocked microphone costs narration, not the session.
`fix_link` is a Windows Settings deep link for the HUD's "Fix" button (ST-072). Messages describe Windows
settings only and never contain anything captured (INV-10).

### `get_diagnostics` → `diagnostics`

ST-071's panel, answered by the service because the UI has no store, no hooks and no counters of its own.

```json
{ "type": "diagnostics", "request_id": 7, "scope": "Capturing - ScreenConnect",
  "microphone": "Headset Microphone (Realtek)", "suppression": "password_field",
  "redaction_backlog": 4, "frames_dropped": 0, "keystrokes_dropped": 17, "egress_blocked": 0,
  "name_hook_refusals": 0, "local_only": true, "policy_version": "local", "cpu_percent": 3.4,
  "working_set_bytes": 251658240, "service_version": "0.1.0" }
```

Every field is a state, a count or a device name. `scope` is the scope decision's own words (a process
name and what is being done about it), never a window title, because this text goes on a clipboard and
into tickets (INV-10). `suppression` is absent when nothing is suppressing capture. `redaction_backlog`
is the depth of the in-memory queue (ADR-0006); `frames_dropped` counts frames lost unchecked (INV-1),
whether deleted because they could not be read or refused because that queue was full. `frames_after_discard` (v6, optional, 0 when absent) counts frames read and masked for a session discarded meanwhile, which the store refused to keep: neither lost unchecked nor stored. `name_hook_refusals` (v8, optional) counts the times Windows refused to narrow the foreground watcher's title-change hook to the window in front, so that it listened to every window instead (ST-031): nothing is missed, but the idle service wakes for every rename on the desktop. A count only, nothing about any window (INV-10). It is absent when the service cannot know it, before the watcher has started, and the panel then says unknown rather than 0; the panel shows it as "Hook fallbacks", with what it means beside a count above 0. `local_only` and `egress_blocked` come from the same policy object the
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

```json
{ "id": "s2", "started_at": "2026-09-15T14:20:00Z", "duration_ms": 3720000, "status": "published",
  "tool": "rdp", "frames": 25, "frames_purged": 0, "publish_unconfirmed": false,
  "title": "Outlook keeps asking for the password", "ticket": "50117", "company": "Acme Dental" }
```

`title`, `ticket`, `company` (strings, v11, each optional and absent when unknown; spec v0.6.9 S4):
`title` is the drafted note's own title (the draft's `suggested_title` as last saved), never a window
title; it is absent before there is a draft. `ticket` is the ticket a publish may have reached, else the
digits read off the window at start (`suggested_ticket`, ST-077), else absent. `company` is the PSA's name
for that published ticket's company, as the ticket search returned it (`publish_session`'s
`ticket_company`); the store keeps it (migration 0015) only once a destination of a publish landed or is
unknown, never from a search alone, so it is absent before a publish. None of the three is sent for a
`discarded` session, and a discard clears them in the store (INV-12). They are shown on the technician's
screen and never written to a log or a metric (INV-10). A UI shows a row without them as it did in v10.

`publish_unconfirmed` (bool, v10; the service writes it on every row, true or false): a publish of the session may have reached the
ticket and was never confirmed (the store's unconfirmed mark, migration 0013). It is beside `status`,
never one of its values: the session is still a `draft` that may be discarded, or `published` in part
and kept, and the filters and the discard rule go on reading `status` alone. History draws it as a
second badge, "Not confirmed", and offers Mark as published for it. Never true for a `discarded`
session, whose row may still carry the mark: discard is the last word (INV-12).

### `request_confirmation` → `confirmation`, then `discard`, `discard_sessions` or `erase_all_local_data`

ST-085. Spec §3 has asked for a typed confirmation before an irreversible delete since it was written;
until 2026-09-20 the tray menu threw a session away on a single click, one item below "Stop and draft".

```json
{ "type": "request_confirmation", "request_id": 9, "action": "discard_session" }
{ "type": "confirmation", "request_id": 9, "token": "…", "phrase": "discard" }
{ "type": "discard", "request_id": 10, "confirmation": "…" }
```

Review and History name their sessions (v6):

```json
{ "type": "request_confirmation", "request_id": 11, "action": "discard_sessions", "session_ids": ["s1", "s2"] }
{ "type": "confirmation", "request_id": 11, "token": "…", "phrase": "2" }
{ "type": "discard_sessions", "request_id": 12, "session_ids": ["s2", "s1"], "confirmation": "…" }
```

A named token comes from its own issuer and is bound to the sorted ids as well as to the action and the
connection. Presented for fewer, more or other ids it is refused and spent, so the sessions it was earned
for need confirming again; a tray token is no use on `discard_sessions`, and a named one is no use on
`discard`.

**What a token proves.** That the command comes from the connection that asked, for exactly what it
asked about, within 30 seconds. It does not prove the phrase was typed: the typed check is the dialog's,
in the UI, as it is for the tray's discard and for erase, and a same-user process that speaks the
protocol can ask for a token and spend it without one (the threat model in `Confirmations`).
`discard_sessions` waits on the connection's worker queue, so a token can go stale while it waits behind
slow commands (a publish, a ticket search); the service then refuses it with "The confirmation was not
for these sessions, or it ran out, so nothing was discarded. Try again.", and nothing is discarded.

**What a discard cannot recall.** A draft or a publish whose request is already on its way to the
backend at the moment of the discard is not stopped: the discard deletes the queued item, and what comes
back for the session (a drafted note, a published state) is refused, but the bytes already sent were
sent. Anything still queued goes with the discard and is never sent.

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
an indicator: the service counts pills it has heard from, not clients it can see (INV-4, #130). While
none is reported, `IndicatorGuard` suppresses capture after a short grace for UI restarts.

A report with a zero width and height means the technician chose tray-only between sessions, and the
pill returns as a session starts (owner decision, 2026-10-25). INV-4 allows tray-only as the technician's
choice, and the report keeps a session the service starts by itself from being refused for want of the
pill they put away. Once a session is recording, paused or suppressed, a zero-size report no longer
counts: only a report with the pill's rectangle does. The pill has the same grace a UI restart has to
come back, counted from the moment the session started (not from the report's time), and if no real
report arrives within it, because the pill failed to come back or came back on no monitor,
`IndicatorGuard` suppresses capture as it does for a missing pill. The UI sends the zero-size report the
moment it puts the pill away, so it replaces the pill's last report rather than leaving it current. A
session that starts on the pill put away therefore runs without a pill for at most that grace. A zero-size
report that arrives later in a session, from a UI that missed the state change, is not the pill either,
and since the grace from the start is long over, capture is suppressed at once until a real report comes.
A width or height that is not above zero reads as zero. A pill that is up but on no monitor is not
reported at all. A pill that was reported and then stops being reported (the UI froze or crashed, or its
monitor went) is noticed as before, when its last report expires, so the outer bound for any pill lost
during a session is still one report's lifetime (six seconds) plus the grace. The wire format is unchanged
and the version is not bumped.

The same reports gate `start`: with none current the service answers a failed `result` and starts
nothing, as it does for a session it would start by itself. A zero-size report counts for a start, as
above, on every start path: the pipe's, the hotkey's and the automatic one.

### Review over the pipe: `get_session`, `get_frame`, the edits

ST-085's remainder (2026-09-25). The UI has no store, and until these existed the Review views were fed
only by the screenshot harness: the shell's Review area showed the word "Review". Everything a technician
does to a session in Review is a question or a command here, and the service (the one process that
reads a frame) does the work.

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
only then answered, so a crash between the write and the reply leaves the disk ahead of the screen,
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
is. A refusal (no PSA connected, a revoked device, a backend that did not answer) arrives as a failed
`result` carrying the backend's own words, which the pane shows beside the button. The UI reads the
reason off that one reply. It never sends `publish_session` again to learn it, because a second send is a
second publish; `activate_device` and `check_integration` are read the same way (2026-10-14, S-36).

**A publish that may have landed** (2026-11-08, ST-093). Six answers mean "sent, or once sent, and
nobody can say whether it is on the ticket": a `kind: unknown` or `kind: timed_out` row from the backend,
a pipe that dropped after `publish_session` went out (the UI's `AskAsync` answers lost, and `PipePublisher`
turns it into a `kind: unknown` row for every destination, with the backend's own "Check the ticket
before you publish ... again" wording), and the service's three refusals below (already published,
not confirmed, still publishing). The pane shows those rows as not
confirmed, never as failed, with a warning and a checkbox ("I checked ticket #48213 and want to publish
again"); Publish, `Ctrl+Enter`, Retry and the mapping prompt's retry all wait for the box, and the tick is
spent on one send, including a tick given while that send was on its way (the box is disabled meanwhile).
The warning stays up once the box is ticked; only the buttons change.

**What the service remembers** (owner decisions O2, O3, O4 on #265). A third refusal joins the two
below: a destination whose last publish may have landed is refused with "An earlier publish of this
session was not confirmed, so it may already be on the ticket. Check the ticket, then confirm before
publishing again, so nothing is added twice." unless `publish_again` is true. The record is kept in the
store, per destination, not as a session state (History's filters, discard and retention do not change,
and discard is not blocked by it), so it survives a reopened Review and a service restart. The service
checks each destination a `publish_session` names against it (re-review of #265, MJ1): without
`publish_again`, any named destination held as `landed` is refused as already published, any held as
`unknown` as not confirmed, and the others go. Every window is held to the same record, and the UI
sends `publish_again` only once its technician ticked the box: a Retry of what plainly failed beside a
landing goes without it, since nothing of it is held (re-review of #265 at bde6a99; this withdrew the
earlier silent flag on Retry). A session published before the record existed (migration 0013) has no
outcomes: a plain send of any destination is refused as already published, and the first publish of it
writes every destination as `unknown` before laying its own answer over them, so the doubt outlives it.
A record that holds a landing for a session not marked published (a service that died between the two
writes) is mended at the next publish of that session, which marks it published first. A destination
name other than `ticket_note`, `time_entry` and `kb_article` is refused before anything is written. The service writes each
destination it sends as `unknown` before asking the backend, so a fault, a cancelled connection or a
service that dies mid-publish leaves the mark, then replaces it with the answer: a destination that landed
stays `landed`, an `unknown` or `timed_out` row stays `unknown`, a plain failure drops out, and a plain
refusal puts the record back as it was. The mark is cleared once no destination is `unknown`.

On opening Review, and again after the service refuses a session already published or unconfirmed (the
pane asks `get_session` for `publish_outcomes`), the pane offers each destination as the record left it:
`landed` unticked and marked "already on the ticket" ("already in the knowledge base" for the
article), `unknown` unticked and marked `?` (read as "not confirmed"), and the rest as they were: on
opening Review, the note and time entry ticked and the article as the draft's `kb_candidate` suggests.
Sending a `landed` or `unknown` one needs the box, whether or not the pane has results of its own;
sending only the others does not, and goes without `publish_again`. Retry waits for the box too while
a failed destination has an earlier `landed` or `unknown` outcome, as after a whole-request refusal.
After the service's refusal the pane says that refusal in the form's live region. When that `get_session`
comes back with no outcomes (a dropped pipe, a session gone), the refusal rows stay as they are, so the box
shows and Publish never loops on a silent refusal. A whole-request refusal ("The backend did not answer.")
says nothing about any destination, so an earlier `unknown` stays.

The service's gateway reads a failed publish the same way, when in doubt `unknown`: once the request may
have left (the HttpClient timeout, any `HttpRequestException` other than `NameResolutionError`,
`ConnectionError` or `SecureConnectionError`, any 5xx other than the backend's own 501 for nothing
connected (`no_psa`, `no_docs` or `not_configured`), a 2xx
other than 200, or a 200 whose answer cannot be read or breaks off) every destination comes back
`kind: unknown`, not retryable, with check-first wording, and the audit log gets the same
`bundle_sent` row (session id, bytes, host) an answered publish writes. Not enrolled, no backend, the
egress guard, a connection that never opened and any 4xx stay plain refusals with no row, because nothing
was sent or the backend refused before acting. `ConnectionError` is raised only while connecting, and the
handler never retries a POST with a body on HTTP/1.1; `PsaGateway.NeverSent` cites the .NET 10 source.

The backend keeps nothing to recognise a second copy by (INV-7), so the service is the second guard. A
destination it holds as landed is refused with "This session is already published, so it may already be
on the ticket. Check the ticket, then confirm before publishing again, so nothing is added twice." unless
`publish_again` is true, which the UI sets only after the technician ticked the box. A second `publish_session` for a session whose first has not answered yet is refused with "This
session is still being published, ..." whatever `publish_again` says, because the ticket the technician
just looked at may not show the first one yet. The pane reads both refusals as the may-have-landed outcome,
so the technician is asked rather than told; the first is said as "Already published: check the ticket
before publishing again" rather than "not confirmed", because part of it is on the ticket. The service
keeps a refusal's words for its `result` by connection and request id, since each client numbers its
requests from 1. `publish_again` is optional and false when absent; it is a
new field on a command both sides exchange, so the version moved to 9.

**Settling it without publishing again: `mark_published`** (2026-11-10, v10). The other way out of
"not confirmed": the technician looked at the ticket, the note is there, and nothing should be sent
again. History offers it for one selected session whose row says `publish_unconfirmed`, asks first (a
plain confirmation that names the session as its row shows it, Cancel the default; it is a statement
about the ticket, which ScreenTail cannot check; nothing is asked while a session is being captured,
and nothing is sent if one began while the question was up), and sends `mark_published` with the
session's id. The service takes the technician's word, as it does for the "I checked" tick:

- Every destination held as `unknown` becomes `landed`; one held as `landed` stays, and one never sent
  stays absent, so it can still be published for the first time without `publish_again`. The mark is
  cleared with the last `unknown`, and `publishes_unconfirmed` is told again.
- The session is marked published by the same write a landing makes (`MarkPublishedAsync`), and the
  machine is told, so it stops resting on it. That write goes first: it refuses a row discarded in the
  meantime, and a service that dies between the two writes leaves the session published and still
  marked, which History goes on showing and a second `mark_published` finishes.
- A session with no destination on record as `landed` is reported to metrics and the style learner as
  published now; one with a landing on record was reported when it landed and is not reported again.
  Read from the record, not the state, so a mark cut off between its two writes is still reported by
  the second `mark_published` that finishes it. The metric is queued through the outbox only when
  telemetry is on, under the key `metric:{session}:published`, so it is posted to the backend once
  however many times it is queued. The style learner reads and writes the local store only.
- Refused, each with its own fixed sentence and nothing changed: no destination is held as `unknown`
  ("No publish of this session is waiting to be confirmed, so nothing was changed."), which covers a
  draft never published, a clean publish, a session already settled, and one published before 0013
  that has no record (nothing says which destinations it would be claiming; one new publish of it
  writes them down and it can then be settled); the session was discarded (INV-12); a publish of it has
  not answered yet (its answer will say what happened); the session is gone.
- No confirmation token: nothing is deleted, and nothing is sent to the ticket or the PSA. The one
  thing that can leave the machine is the session metric above, queued when telemetry is on. A same-user process that
  speaks the protocol can send it, as it can send `publish_session` with `publish_again`.
- A published session is kept until retention and cannot be discarded, so marking a draft in doubt as
  published also ends its discard. The dialog says so.

```json
{ "type": "mark_published", "request_id": 24, "session_id": "s1" }
{ "type": "result", "request_id": 24, "ok": true }
```

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
- Every other command, including any added later, goes to one queue per connection (`discard_sessions` among them, v6: it may wait on the machine while a session finalizes, and the heartbeat must not wait with it) and is run one at a time in the order it was sent, as every command was before. At most 16 wait behind the one running; one more is answered at once with `ok: false` and a "busy" sentence rather than read later. When the client disconnects, whatever is still waiting is dropped, not run.
- The level meter's `get_microphone_level` and `stop_microphone_level` (v7) queue like the rest, on purpose: closing a device can wait on a driver, and the heartbeat must not wait with it. A request held behind a slow command lets the meter's lease run out, which closes the device; it can never hold it open. The UI sends the next request only once the last is answered, so the meter puts at most one in the queue.
- If a command on either lane fails, the service closes the connection at once, even while a queued command is still running, so the client sees the pipe end and reconnects. The running command finishes, but its reply goes nowhere.
- An answer too large for one frame is not such a failure, on either lane: it is refused before a byte is written, with a failed `result` that gives its size, and the connection stays up (E-2). `get_session`, `get_frame`, `get_frame_thumbnail` and `get_frame_text`, the replies most likely to be large, all queue.

Before this a slow backend call (a publish, a ticket search) held up the HUD's `indicator_showing` past the indicator's grace, and the service dropped frames and typing as unindicated with the pill on screen (INV-4, INV-6). The wire format is unchanged and the version is not bumped: the client has always matched replies by `request_id`.

## Authentication

Details in ADR-0003. In short: the pipe admits only the same user; the service checks the client executable (signed by the same publisher, or same directory for unsigned dev builds); the UI checks the pipe server the same way before writing the token; and the client presents the per-service-run token from `%LOCALAPPDATA%\ScreenTail\ipc.token`, a file only that user can read. Rejections are audit-logged as `ipc_rejected_<reason>`, nothing else.

## Reattach

The service keeps all state. A reconnecting client sends `hello` and gets the state back in `hello_ack`. Budget: 2 s from UI restart to reattached; measured well under 100 ms.

A client that cannot be told a `state_changed` is made to reattach. `state_changed` is the only event the service volunteers, and it is sent to each client with a deadline of five seconds. When the write fails or misses its deadline (the client has stopped reading, or a large reply to it holds the connection's write lock), the service closes that client's connection rather than drop the snapshot and keep the pipe open. A broadcast ended only because the service's own token was cancelled (the service stopping, which closes every connection itself) closes nothing (owner's decision, 2026-11-08). The UI then shows the last state it knew as unknown ("may still be recording", never idle), reconnects at once, and draws the current state from `hello_ack`. The service never waits on the client to read: the close is a handle closed, and nothing in the service's stop depends on any client.

## Connection limits

The handshake table holds 16 in flight and a peer has two seconds to say `hello`. When the table is full
the **oldest silent** handshake is dropped to make room, and the newcomer is never refused. Refusing the
newcomer was the earlier policy and it handed a same-user process a better attack than the one it fixed:
sixteen silent connections kept the table full, every refusal fell on whoever asked next, and the real UI
was the one turned away, unable to see or change capture state, which defeats INV-4 by making the
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
| 2026-10-29 | `draft_pending` state on the snapshot; `draft_pending` on `session` (ST-013) | **5** |
| 2026-10-29 | `get_update` / `update`, `install_update`, `set_update_channel` (ST-113). New commands, and `update` is only ever a reply to `get_update`, so they bump nothing; they arrive in v5 alongside ST-013's change | 5 (not bumped by these) |
| 2026-10-31 | `session_ids` on `request_confirmation`, the `discard_sessions` action and command: Review's Discard and History's bulk discard by name, the token bound to exactly the ids (ST-074, ST-079). A new field on a command both sides exchange, so the version moves; the handshake still requires an exact match. Also in v6: `frames_after_discard` on `diagnostics` | **6** |
| 2026-10-31 | `microphones[]` as rows (`id`, `name`, `is_default`) on `capture_settings` and `microphone_id` in the capture settings document: the microphone is chosen in Settings, Capture (ST-080 AC3). A changed reply and a new member on a document both sides exchange, so the version moves. Also in v7: `get_microphone_level` / `microphone_level` and `stop_microphone_level`, the level meter, a lease the service ends by itself | **7** |
| 2026-11-04 | `name_hook_refusals` on `diagnostics`: how often Windows refused the foreground watcher's narrow title-change hook, absent when the service cannot know it (ST-031). A new member on a reply, which an older UI would fail on, so the version moves | **8** |
| 2026-11-05 | `state_changed` is also volunteered when the redaction backlog (`pending_redactions`) moves and the state does not, as it already was when `drafts_ready` moved, so the pill's count follows the worker (ST-072). The same message and shape; told under the machine's transition lock, so it is never overtaken by a transition, and coalesced while a transition holds it. In practice at most about once a second, because the redaction worker reports its backlog on a timer (`RedactionWorker`'s `ReportEvery`, 1 s). An older client draws it as it draws any snapshot, so the version is not bumped | 8 |
| 2026-11-08 | A `state_changed` the service cannot write to a client within five seconds (the client is not reading, or a large reply to it holds the connection's write lock), or whose write fails, closes that client's connection instead of being dropped with the connection kept (not when the service's own token cancelled it, as it does when stopping). The client sees the pipe end, shows the state as unknown, reconnects and is told the current state in `hello_ack`. Before this a dropped "paused" or "finalizing" left the UI showing Recording with its clock ticking (#262 review, finding 8). The service never waits for the client to read: closing is a handle closed, and its stop does not depend on it. No message changes, so it adds no bump of its own; it ships with v9 | 9 |
| 2026-11-08 | `publish_again` on `publish_session`, optional, false when absent: without it the service refuses a destination it holds as landed (or, for a session published before 0013, any destination), and it refuses a second publish of a session while the first has not answered. A lost answer to a publish is read by the UI as `kind: unknown` per destination and held until the technician confirms they checked the ticket (ST-093). Also in v9, before release: `publish_outcomes` on the `session` reply, what earlier publishes left per destination, and the service's refusal of a session whose last publish may have landed (O2, O3), checked for each destination named (re-review of #265). A new field on a command both sides exchange, which an older service would refuse as an unknown member, so the version moves | **9** |
| 2026-11-10 | `publish_unconfirmed` on a `sessions` row and `publishes_unconfirmed` on the state snapshot: History's "Not confirmed" badge beside the status and the tray's line between sessions, for a publish that may have reached the ticket (ST-093). New members on replies both sides exchange, which an older UI would fail on, so the version moves. Also in v10: `mark_published`, which records the destinations held as `unknown` as `landed` on the technician's word, clears the mark and marks the session published without sending anything; refused for a session with nothing held as `unknown`, a discarded one, one still being published and one that is gone. A new command, which would not have moved the version on its own | **10** |
| 2026-11-12 | `title`, `ticket` and `company` on a `sessions` row, each optional and left off when unknown: the inbox rail's rows say the company, the note's title and the ticket (spec v0.6.9 S4, ST-016). The company is kept from a publish that may have reached the ticket (store migration 0015), never from a search alone; nothing for a discarded session. New members on a reply both sides exchange, which a v10 UI would fail on, so the version moves | **11** |
