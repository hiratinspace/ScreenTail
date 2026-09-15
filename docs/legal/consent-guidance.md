# Consent guidance for technicians

**This is not legal advice, and it has not been reviewed by a lawyer.** It is a summary of how ScreenTail
behaves, written so that your counsel has something concrete to react to. Have them read it before you
put any of it in front of a customer. Where it states a legal position, treat that as a question to ask
them, not an answer from us.

---

## The fact that changes the question

**ScreenTail does not record the customer in v1.**

Only the technician's microphone is captured (INV-9), and there is no code path that produces any other
speaker — the field exists in the data format and is reserved for a future consent workflow, but nothing
can write it. End-user audio is v1.2 and gated behind ST-010's consent flow, which is not built.

This matters because most of what people mean by "recording consent" is **wiretap and eavesdropping law**,
which is about intercepting the *other party's* communications. A technician narrating their own work
into their own microphone is much closer to dictating a note than to recording a call.

What ScreenTail *does* capture of the customer is **their screen** — which is a data-protection and
contractual question rather than a wiretap one, and a different conversation with your counsel.

## The two-party question, and why it is still worth asking

Some US states require **all parties** to consent before a conversation is recorded. Which states, and how
their statutes treat a one-sided narration, is exactly what to ask counsel — the answer turns on whether
what ScreenTail captures counts as a "communication" at all when the customer's audio is never recorded.

The practical position we would suggest putting to them:

- **v1 (today).** The technician's own voice, on the technician's own machine. Ask counsel whether your
  standard MSA already covers it, and whether any customer contract says otherwise.
- **v1.2 (end-user audio).** This is the version where all-party consent becomes squarely relevant. The
  consent workflow is designed to ask, record the answer and the script version in the audit log, and
  refuse to capture end-user audio without it.

Do not enable end-user audio, when it ships, on the strength of this document.

## What to tell a customer who asks

A technician does not need a script for a legal position. They need a true sentence and somewhere to point.

> "I'm using a tool that takes screenshots of this session so I can write the ticket up properly. It
> doesn't record you or your microphone, it blanks out things like passwords and card numbers
> automatically, and nothing gets saved to your ticket until I've read it myself."

Every clause of that is enforced and checkable:

| Claim | Enforced by |
|---|---|
| takes screenshots of this session | scope defaults to the remote tool and admin tools (INV-5) |
| doesn't record you | only the technician's microphone (INV-9) |
| blanks out passwords and card numbers | on-device redaction before any frame is readable (INV-1) |
| nothing saved until I've read it | no automatic publishing exists (INV-3) |

If the customer wants to see it, the technician can open the diagnostics panel — it shows what is being
captured right now and holds no content, so it is safe to turn the screen round.

## If a customer says no

There is a real answer, and it is not "carry on":

- **Pause capture** — `Ctrl+Alt+P`. The pill shows paused, and no screenshots or events are recorded
  while it is.
- **Discard the session entirely** — from the tray or the review screen. The screenshots, transcript and
  timeline are deleted immediately, and the audit log records that they were.
- **Exclude an application** before starting, if the objection is to one system rather than the session.

## What to put in your own paperwork

Questions for counsel, not answers from us:

1. Does your MSA already permit taking screenshots of a customer's screen during support, and for how
   long may the material be kept? ScreenTail's default is 7 days, configurable 1–30.
2. Does any customer sit under a regime — health, financial, legal, government — where screen capture
   needs specific handling, and should those customers be on local-only mode so nothing leaves the
   technician's device at all?
3. Who at your MSP can see a published note, and does that match what the customer was told?
4. When end-user audio ships, which states' all-party rules apply to your technicians and your customers,
   and does the consent script satisfy them?

## Where the details are

- [What ScreenTail does with your data](../security/data-flow.md) — plain language, for customers.
- [Threat model](../security/threat-model.md) — what is defended, and what is not.
- [Security questionnaire](../security/questionnaire.md) — pre-filled answers for procurement.
