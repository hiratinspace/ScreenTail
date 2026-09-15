# Security questionnaire — pre-filled answers

For procurement and vendor-review forms. Every answer names what enforces it, so a reviewer can check
rather than take our word for it.

**Answers marked ⚠ describe something not yet built.** They are written as "designed to" rather than
"does", because a questionnaire that overstates a shipped control is the one document that will be held
against you later. Do not delete the marks before the tickets close.

Status as of this document: the Windows client is built; publishing and the hosted backend are not.

---

## 1 · Data collection

**1.1 What data does the product collect?**
Screenshots of the technician's screen during a remote-support session, a timeline of input *categories*
(not keys), the technician's spoken narration as text, and a drafted ticket note.

**1.2 Does it record keystrokes?**
No. The keyboard hook emits categories and counts — a typing burst of *n* characters, a shortcut, Enter —
and no field in the data format can hold a key or character. Enforced by the hook and independently by the
schema (`additionalProperties: false`), so a future change cannot add one quietly. *(INV-2)*

**1.3 Does it record the customer's voice?**
No. Only the technician's microphone. There is no code path that produces any other speaker. *(INV-9)*

**1.4 Does it record video?**
No. Still screenshots, taken on click or on significant screen change.

**1.5 How often are screenshots taken?**
On technician clicks (debounced) and on scene change, with a per-session budget. Not on a timer and not
continuously.

**1.6 Which windows are captured?**
By default, the remote-support tool plus an allowlist of admin tools. Capturing all windows is opt-in and
warns that other customers' data may be on screen. *(INV-5)*

**1.7 ⚠ Can capture be running without the user knowing?**
**Today, yes — there is no capture indicator in the shipping build.** This answer previously claimed a
tray icon; that was wrong, and no tray icon exists in the codebase at all.

The on-screen indicator is built, is designed so it cannot be dismissed mid-session, and has tests and
rendered screenshots — but the UI process does not connect to the capture service, so nothing shows it.
`TrayPresence` computes what a tray icon would display and nothing renders one.

**Do not deploy to a customer-facing pilot until this is closed.** INV-4 is the invariant that makes the
product's premise defensible, and it is currently unenforced. *(INV-4; tracked as the top item in the
weakness review.)*

**1.8 ⚠ Is the indicator visible to the customer during screen sharing?**
The indicator sets `WDA_EXCLUDEFROMCAPTURE`, so a screen-share viewer would not see it. Moot until 1.7
is closed: there is no indicator on screen to exclude.

**1.9 What happens when a password field has focus?**
Capture stops. No screenshot and no typing event is recorded for that interval — the data is dropped, not
hidden. Detection is by UI Automation, verified on real hardware. *(INV-6)*

**1.10 What other conditions stop capture?**
An excluded application in front, a window running elevated, or a screen that matches sensitive-context
heuristics. All drop data the same way.

---

## 2 · Data handling on the device

**2.1 Where is data stored?**
An encrypted SQLite database (SQLCipher) in the technician's own user profile.

**2.2 How is the encryption key protected?**
Windows DPAPI, scoped to that user account.

**2.3 Can a screenshot be read before it is redacted?**
No. Frames are written with `redaction_pending` set and only the redaction worker can read one back. Every
other path filters on redacted frames. *(INV-1)*

**2.4 What if redaction fails on a frame?**
The frame is deleted. An unreadable frame retained is an unredacted frame.

**2.5 What is redacted?**
Card numbers (Luhn-checked), national ID numbers, API keys and tokens, private key blocks, password
fields, and tenant-defined patterns. Masking happens on the device before any frame becomes readable.

**2.6 Does redaction run locally or in the cloud?**
Locally, always. OCR uses the Windows on-device engine.

**2.7 How long is raw data kept?**
Default 7 days, configurable 1–30. Screenshots, transcript and timeline are deleted on that clock; the
drafted note survives. *(INV-12)*

**2.8 Is there a delete-everything option?**
Yes, in Settings, behind a typed confirmation. It removes tokens as well as data.

**2.9 Is deleted data recoverable?**
No. Deletion is a hard delete followed by a vacuum. A blurred region is flattened to solid colour and the
original bytes are overwritten in the same transaction — nothing retains a pre-blur copy.

**2.10 Can a technician delete an individual screenshot?**
Yes, with a five-second undo. After that it is permanent and an audit row records it.

---

## 3 · Data leaving the device

**3.1 What leaves the device?**
Only a redacted bundle sent to produce the draft note, and only what the technician publishes.

**3.2 Can the product publish automatically?**
No. There is no automatic publishing path. *(INV-3)*

**3.3 Is there a mode where nothing leaves the device?**
Yes. Local-only mode drafts on the device. It is enforced by an HTTP allowlist that defaults closed and
takes the destination's purpose from the calling code rather than inferring it from the URL. *(INV-8)*

**3.4 ⚠ Can an administrator lock local-only mode on?**
Designed to, via tenant policy enforced client-side rather than merely displayed. *(INV-11; ST-047 not yet
implemented.)*

**3.5 ⚠ Does the backend store screenshots?**
Designed not to: frames are held in memory for one request and never written to storage, and the
backend's tests assert its storage does not grow. *(INV-7; the backend is not yet deployed — ST-007.)*

**3.6 ⚠ What is the hosted model provider?**
Not yet selected. *(ST-063.)*

**3.7 Do logs contain customer data?**
No. Logs and metrics have no content-carrying fields — no window titles, OCR text, transcript, note text,
company or ticket names. The diagnostics record has nowhere to put them, which is stronger than scrubbing
them out. *(INV-10)*

**3.8 ⚠ What does telemetry send?**
No telemetry is sent: there is no metrics system in the client. The Settings screen that would list the
fields does not exist either.

**3.9 ⚠ Is the diagnostics panel trustworthy?**
**Not yet.** It renders a hard-coded sample — including `Local-only: true` and `Egress blocked: 0` — because
nothing populates it from live state. It must not be shown to a customer as evidence until it is wired to
the running service.

---

## 4 · Access and authentication

**4.1 How do the product's components talk to each other?**
A per-user Windows named pipe between the capture service and the UI. No network socket is used, and no
component listens on a TCP port — asserted by a test that fails the build if a listener type appears in
client source.

**4.2 How is that channel authenticated?**
Three checks: a per-user ACL on the pipe, a per-run token that is never written to disk, and verification
that the connecting executable is Authenticode-signed by our publisher.

**4.3 Is the signature actually verified, or just present?**
Verified with `WinVerifyTrust`, which hashes the file and walks the chain. An earlier implementation
compared the certificate thumbprint, which a modified binary can satisfy by copying the certificate blob;
that was replaced and there is a test that tampers with a signed binary and asserts refusal.

**4.4 ⚠ Does the UI verify the service, or only the other way round?**
The service verifies the client today. The reverse check is built — the UI's connect call **requires** a
server verifier rather than defaulting to none, so it cannot be omitted by accident — but the UI does not
yet connect to the service at all, so nothing exercises it in a shipping build.

**4.5 Are debug or diagnostic endpoints present in release builds?**
No. There is no build-conditional code in the client at all, asserted by test — release and debug contain
the same code.

**4.6 ⚠ Are binaries code-signed?**
Designed to be; signing is part of the release pipeline, which is not yet built. Unsigned development
builds fall back to requiring the peer executable to sit in the same directory.

---

## 5 · Auditing

**5.1 Is there an audit log?**
Yes, per session: frames captured, frames deleted unredacted, suppressed intervals with reasons and
durations, redactions by kind, bytes sent and to which host, and every deletion.

**5.2 Can the audit log be tampered with?**
Each row carries the hash of the previous row, so removing or editing one breaks every row after it.
`VerifyAuditAsync` walks the chain and reports whether it is intact, how many rows were checked, and the
id of the first row that does not follow from its predecessor.

**5.3 Does the audit log contain customer data?**
No. Rows hold a kind, a count, a timestamp and an optional short detail from a fixed vocabulary. There is
no free-text field.

**5.4 Does the audit log survive deletion of the session?**
Yes, deliberately. Discarding a session or purging it on retention keeps the audit rows, so there is a
record that data existed and was destroyed. *(INV-12)*

**5.5 Can the audit log be exported?**
Yes, JSON or CSV, with the verification result included.

---

## 6 · Product behaviour a reviewer should know

**6.1 What does the technician see before anything is published?**
The drafted note, every screenshot with include/exclude control, the transcript, and the timeline. Steps
the model inferred rather than observed are marked, and the marker clears only when the technician edits
or confirms them.

**6.2 Can the drafted note contain something nobody said?**
The drafting prompt is instructed that OCR text and transcript are evidence rather than instruction, and
post-conditions refuse invented quotations, credentials and instruction-shaped text in every free-text
field. Quotations are checked against the transcript.

**6.3 Could text on a customer's screen influence the note?**
This is treated as a prompt-injection surface. Directives, URLs and command lines are refused in every
field of the draft, not only in the steps.

**6.4 Is the transcript trustworthy?**
Speech recognition can produce fluent text for silence. Segments that look invented — boilerplate phrases
on short or low-confidence audio, or text too long for the audio that carried it — are dropped before
storage, because a hallucinated line would otherwise make a fabricated quotation verifiable.

**6.5 What happens if the machine cannot capture?**
The capability probe reports what Windows allows and the indicator says so. It checks the window station
and session, so a service with no desktop is reported as unable to capture rather than as healthy.

---

## 7 · Compliance and process

**7.1 ⚠ Do you have SOC 2 / ISO 27001?**
No. Not yet pursued.

**7.2 ⚠ Do you carry cyber liability insurance?**
To be answered by the MSP deploying ScreenTail.

**7.3 Is there a documented threat model?**
Yes — [threat-model.md](threat-model.md), STRIDE, with each threat mapped to the invariant it protects and
the ticket that mitigates it, plus an explicit list of what is *not* mitigated.

**7.4 Has this pack been reviewed by a security professional?**
⚠ **Not yet.** ST-114 requires review by an MSP security lead and that has not happened. Do not represent
this pack as reviewed until it has.

**7.5 Where is consent guidance?**
[consent-guidance.md](../legal/consent-guidance.md). It is explicitly not legal advice and is written to be
taken to counsel.

**7.6 Who do we contact about a vulnerability?**
⚠ To be filled in by the MSP deploying ScreenTail. A disclosure contact is not yet published.
