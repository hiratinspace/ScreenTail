# What ScreenTail does with your data

For an MSP evaluating ScreenTail, and for the customer who asks what that tool on the technician's screen
is. Written in plain language on purpose: a data-flow document nobody outside engineering can read is a
document that gets forwarded and never checked.

Everything here is enforced in code and named against the invariant that enforces it. Where something is
not yet built, it says so.

---

## The short version

ScreenTail watches a remote-support session on the **technician's** machine, turns it into a draft ticket
note, and shows that note to the technician before anything leaves the device. It never publishes on its
own.

```
  Technician's machine                                          Your PSA
  ┌─────────────────────────────────────────────────┐          ┌──────────┐
  │                                                 │          │          │
  │  screenshot ──► encrypted store ──► redaction   │          │ ticket   │
  │  (on click or   (unreadable       (masks what   │          │ note     │
  │   scene change)  until redacted)    it finds)   │          │          │
  │                                        │        │          └──────────┘
  │  microphone ───► transcript ───────────┤        │               ▲
  │  (technician only)                     │        │               │
  │                                        ▼        │               │
  │                                  draft request ─┼──► model ─────┘
  │                                        │        │    (or on-device)
  │                                        ▼        │         ▲
  │                                   YOU REVIEW ───┼─────────┘
  │                                   AND PUBLISH   │    nothing goes further
  │                                                 │    without this step
  └─────────────────────────────────────────────────┘
```

---

## Captured on the device

**Screenshots** are taken when the technician clicks, or when the screen changes enough to be worth
recording — not continuously, and not on a timer.

**Which windows** is restricted by default to the remote-support tool plus an allowlist of admin tools
(INV-5). "All windows" exists, is opt-in, and warns that it will capture other customers' data if the
technician has anything else open.

**Keystrokes are never recorded.** The keyboard hook produces categories and counts — "a burst of 14
characters", "a shortcut", "Enter" — and there is no field anywhere in the data format that can hold a
key or a character (INV-2). This is enforced by the schema as well as by the hook, so a future change
cannot quietly add one.

**Only the technician's microphone** is captured (INV-9). ScreenTail does not record the customer's audio
at all in v1. See [consent guidance](../legal/consent-guidance.md) for why that distinction matters.

**Capture stops by itself** when a password field has focus, when an excluded application is in front,
when a window is running as administrator, or when the screen looks sensitive. Those states **drop the
data rather than hiding it** (INV-6): no screenshot is taken and no typing event is recorded for that
interval. The gap is shown in the review screen so the technician can see that something was deliberately
not captured, rather than wondering what went missing.

**While any of this is happening, it is visible.** A pill on screen shows the state, always, including
while the technician is sharing their screen (INV-4). It cannot be dismissed mid-session, and it is kept
out of screen-share and recording software so the customer's view never shows it. There is no
silent-capture mode.

> **Status — read this before relying on the paragraph above.** The pill is built and tested, but the
> shipping application does not show it: the UI process does not yet connect to the capture service. There
> is **no tray icon** either — a component computes what one would display, and nothing renders it.
>
> **So there is currently no capture indicator at all.** An earlier version of this document said the tray
> icon shipped. That was wrong. Until the UI is connected, INV-4 is unenforced and ScreenTail should not
> be run against a real customer session.

---

## Held on the device

The store is an **encrypted SQLite database** (SQLCipher) with a key held in Windows DPAPI under the
technician's own account.

Screenshots arrive in it **unreadable**. A frame is written with `redaction_pending` set, and the only
component that can read one back is the redaction worker. Every other path — the review screen, the
bundle builder, export — filters on frames that have been redacted (INV-1). A frame that cannot be
redacted is **deleted, not kept**: an unreadable frame retained is an unredacted frame.

Redaction runs on the device. It reads the text on each screenshot with the local OCR engine and masks
what matches — card numbers, national insurance and social security numbers, API keys and tokens,
password fields — and then, and only then, the frame becomes readable to the rest of the product.

**Raw data has a deadline.** Screenshots, transcript and timeline are deleted after a retention period
(default 7 days, configurable 1–30). The note survives; the material it was made from does not (INV-12).
"Delete everything" is available in Settings and removes tokens as well as data.

---

## What leaves the device

**Only when the technician presses Publish** (INV-3). There is no automatic publishing path in v1 — not
a scheduled one, not a "publish when confident" one.

For drafting, the note can be produced two ways:

- **On the device.** Local-only mode means **zero egress**, enforced by an allowlist that defaults closed
  and takes the destination's purpose from the calling code rather than guessing it from the URL (INV-8).
  A tenant can lock this on for every machine.
- **By a hosted model.** A redacted bundle is sent for one request. **The backend never writes frames to
  storage** (INV-7) — they are held in memory for the duration of the request and are not persisted, and
  the backend's tests assert that its storage does not grow.

**Logs contain no content** (INV-10). Not window titles, not OCR text, not transcript, not note text, not
company or ticket names. The record the diagnostics panel is built from has nowhere to put any of those,
which is a stronger guarantee than filtering them out. There is no metrics system yet, so "metrics" in
the invariant table describes an intent rather than a shipped component.

*(Status: the diagnostics panel currently renders a hard-coded sample rather than live state, so it is not
yet something to show a customer as evidence.)*

---

## What gets written down about what happened

Every session keeps an audit log: frames captured, frames deleted because they could not be redacted,
suppressed intervals and why, redactions by kind, bytes sent and to which host, and every deletion.

It is **hash-chained** — each row carries the hash of the one before it — so a row cannot be removed or
altered without breaking every row after it. That matters most for the rows recording that data was
*destroyed*, which are the ones somebody would want gone. The export says whether the chain verified, and
contains no content.

---

## What ScreenTail cannot protect you from

Said plainly, because a document that claims everything is covered is not one a security lead should
trust:

- **Anyone with the technician's Windows account.** They can read the store, because the technician can.
  The answer is your own account security and disk encryption, not ours.
- **Anyone running as SYSTEM** on that machine. Likewise, more so.
- **A compromised remote-support tool.** If the tool ScreenTail is watching is itself hostile, it controls
  the screen being captured. Worth saying aloud, since watching one is the product's entire premise.
- **A technician who opens the wrong window.** Scope defaults and suppression reduce this; they do not
  eliminate it. The review step exists partly for this reason — the technician sees every screenshot
  before it goes anywhere, and can exclude or blur any of them.

The full threat model, with mitigations mapped to tickets, is in [threat-model.md](threat-model.md).

---

## Not yet built

This document describes what is enforced today. These are named here rather than implied:

- **Publishing** (ST-077, ST-078) is not implemented. Nothing reaches a PSA yet.
- **The hosted backend** (ST-007 onward) is not deployed. INV-7's assertion exists in the backend's tests;
  there is no running service.
- **Tenant policy sync** (ST-047) is not implemented, so "a tenant can lock local-only mode" describes the
  designed behaviour, not a shipped one.
- **The egress allowlist** (INV-8) is written and tested but **not installed on anything**. No component in
  the client makes HTTP calls today, so nothing escapes — but the mechanism that would stop it is not in
  the path. Treat INV-8 as designed, not enforced.
- **"Delete everything"** is implemented in the store and unreachable: there is no Settings screen and no
  IPC command that calls it.
- **There is no audio capture at all**, so INV-9 is currently true by absence rather than by enforcement.
