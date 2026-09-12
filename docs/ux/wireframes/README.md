# ScreenTail v1 wireframes (ST-014)

These are low-fidelity, clickable wireframes of every v1 client screen (S1–S8), plus the five annotated flows. They're greyscale on purpose: they settle layout, content, states and flow. Visual design comes in ST-015 (hi-fi Review) and ST-016 (tokens and components).

**Open them:** open `index.html` in any browser (`open docs/ux/wireframes/index.html` on a Mac). There's no build step and no network access. In each screen, the dark bar switches between its states, and <kbd>C</kbd> hides the numbered callouts.

| File | Spec | States |
|---|---|---|
| `s1-tray.html` | §5 S1, §2.4 | recording, idle, draft ready, offline, service stopped; quick menu; "What's being captured" panel |
| `s2-hud.html` | §5 S2, §7 | docked, expanded, right-click; gallery of all 8 pill variants |
| `s3-review.html` | §5 S3 | default (low-confidence step, gaps), no ticket, partial capture, frames purged, local draft, offline, draft failed, no PSA connected, published, partial publish failure |
| `s4-history.html` | §5 S4 | sessions (filters, coverage, bulk discard), empty |
| `s5-s7-settings.html` | §5 S5–S7, §3 locked toggle | Capture, Privacy & Redaction, Privacy with admin policy, Integrations |
| `s8-onboarding.html` | §5 S8 | the 7 steps |
| `flows.html` | Scope §6.4 | happy path, sensitive moment, bad draft, missed start, discard |
| `screens/` | — | PNG exports of each screen and key state at 1920×1080 (headless Chrome) |

## Technician session: "accept this note" (ST-014 AC2)

The ticket requires two technicians to complete this task on the Review wireframe without help. Budget 15 minutes per technician.

1. **Setup.** Open `s3-review.html` fresh (reload it) in a desktop browser, full screen, callouts hidden (<kbd>C</kbd>). The timer starts on load and stops on Publish.
2. **Say only this:** "This is a draft ticket note written from a remote session you just did: front-desk printer offline at Acme Dental. Check it, fix anything that's wrong, and publish it to the ticket. One screenshot shows the user's inbox and shouldn't go on the ticket."
3. **Don't help.** If they ask a question, answer "What would you expect?" and write the question down.
4. **Record** on the sheet below. Finishing unprompted means they published with frame 6 excluded and step 2 confirmed or edited.
5. **Debrief (3 questions):**
   - What did you expect the ⚠ to mean?
   - Would you trust this note on a real ticket?
   - Should the recording pill show while you're sharing your screen with a customer? (open question Q1)

| Technician | Finished unprompted (Y/N) | Task time (s) | Frame 6 excluded | Step 2 checked | Questions asked / hesitations | Debrief answers |
|---|---|---|---|---|---|---|
| T1 |  |  |  |  |  |  |
| T2 |  |  |  |  |  |  |

Save the filled sheet as `docs/ux/wireframes/review-sessions.md` and link it from the PR.

## Open questions for the spec

The ticket requires Q1 and Q2 to be answered and written into the spec. Q3–Q6 came up while wireframing. **Decided 2026-09-11:** the owner accepted all six recommendations, and they're now in the spec as v0.4.1 (§9 Amendments). Q1 and Q2 will still be checked with the two technicians in the debrief.

| # | Question | Where | Decision |
|---|---|---|---|
| Q1 | Should the HUD be visible while the technician shares their screen with a customer? | S2 | **Always visible.** `WDA_EXCLUDEFROMCAPTURE`, verified in ST-001 AC3, also hides the HUD from Teams and Zoom screen shares, so the person watching never sees it and the technician always does. Hiding it would add a silent-capture path (INV-4). Confirm with the two technicians. |
| Q2 | Default ConnectWise note type: Internal or Discussion? | S3, S7 | **Internal.** Discussion notes are customer-visible in ConnectWise, and a first draft shouldn't default to customer-facing. Configurable per tenant in S7. |
| Q3 | The tray's "Discard session…" during a recording conflicts with "no modal while recording" (§4) and with "typed confirmation for irreversible deletes" (§3 Modal). | S1, flow 5 | Selecting it **stops capture first** (the session goes to finalizing, so it's no longer recording), then shows the typed DISCARD dialog. That satisfies both rules. |
| Q4 | After "draft failed", can the technician write the note by hand and publish it? | S3 | **Yes, in v1.1.** For v1, Publish stays disabled with the reason "There is no note to publish yet. Retry the draft first." A manual-note path means an empty editor plus the transcript, which is extra scope. |
| Q5 | The elevated-window wording differs: Spec S2 says "Paused: elevated window"; ST-021 says "Elevated window — screen not captured". | S2 | Use **"Paused: elevated window"** in the HUD (it matches its siblings), and keep ST-021's longer text for the tooltip. |
| Q6 | What does the technician type to confirm a bulk discard ("typed confirmation with count")? | S4 | **The number of sessions**, e.g. `3`. It's shorter than DISCARD and proves they read the count. |

## What this doesn't decide

Colour, type scale, icons and the exact spacing belong to ST-015/ST-016. State colours are stood in for by shape (solid, dashed and hatched borders) and always by words, so every state reads without colour, which is also the Spec §7 requirement.
