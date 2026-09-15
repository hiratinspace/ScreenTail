# Market scan and product ideas — September 2026

**Snapshot taken:** 2026-09-15
**Purpose:** record what the market looked like, where ScreenTail's wedge is, and the feature ideas
worth turning into tickets. This is a reference for prioritisation, not a spec. Nothing here is
committed work until it has a ticket in `Build Plan/02-Backlog-v0.4.md`.

**How it was gathered:** two web surveys on 2026-09-15 (about 115 searches and vendor-page reads).
Competitor claims come from vendor pages and press releases, some of them only through search
indexes because the vendor blocked direct fetches (marked *indexed*). Treat every claim as
"what the vendor says", not as verified behaviour.

---

## 1. Where ScreenTail stands against this

The goal — follow a remote-fix session, take relevant screenshots with context, add what the
technician said, and produce a note that can be retrieved later — is already specified in the
Build Plan and the capture half is built (see `docs/STATUS.md`). What is missing is the middle of
the pipeline: audio capture, drafting, publishing, and the UI wired to the service. This document
is about where to aim next, not what to build from scratch.

---

## 2. The market in September 2026

### 2.1 Same shape as ScreenTail

| Product | What it does | Where it differs from ScreenTail |
|---|---|---|
| **Cyft** (cyft.ai) | Desktop app: reads the screen by OS-level OCR, listens to the call, watches typing; activity stream with timestamps and screenshots. A "pod" inside ConnectWise detects the open ticket. Outputs customer-facing, internal and resolution notes, auto time entry, similar-past-ticket correlation. SOC 2 claimed; 30-day default retention (*indexed*). | Cloud processing. ConnectWise only. No stated on-device redaction. Pricing and platform not public. |
| **Vight** (vight.ai) | Started as call intelligence; now a background agent "recording the session while they work the ticket". Notes, PSA time entries, follow-ups, sentiment, coaching scores, KB drafts. ConnectWise, Autotask, HaloPSA; IT Glue, Hudu, SharePoint; most MSP phone systems. Case study claims 5,671 unbilled hours recovered across 67,794 calls. Added "AI-powered Level 2 support" in August 2026. | Cloud. No redaction statement beyond encryption, SSO and IP allow-lists. Real traction and press — the competitor to watch. |
| **IT Glue Cooper Copilot — Smart SOP Generator** (Kaseya) | Chrome extension: Start Capture records screenshots, clicks and keystrokes; live preview with pause/delete; capture stored in the browser; produces an editable SOP in IT Glue. | Browser only. SOP-shaped, not ticket-note-shaped. |
| **Lexful** (GA August 2026) | AI-native documentation platform: raw notes in, structured SOPs out. "Your data never trains any AI model." | No screen or session capture found. |
| Scribe, Tango, Guidde, Folge | Generic click-capture SOP tools. | No PSA or RMM integration. |

### 2.2 Remote tools with a native session-to-summary feature

These are the direct threat to the wedge: if the tool the technician already uses writes the note,
ScreenTail's value for that tool's users shrinks. Every one of them is locked to its own client.

| Tool | Status | What it does | Notes |
|---|---|---|---|
| **Atera AI Copilot** — remote session summary | Shipped; in all plans, free since June 2026 | Summary generated from the Splashtop session ("itemised list of actions performed on the end-user Windows device"), tech name and duration. Only when the tech uses Disconnect, not the window's X. Admins see all techs' summaries; techs see their own. Account toggle; turning it off deletes prior summaries. | Splashtop-only, Atera-only. Says "sensitive data (passwords, addresses) are not included" without saying how. |
| **TeamViewer Session Insights** | Shipped 2024; Business through Tensor; AI-credit pricing | Summary, smart tags (issue type, applications involved), step-by-step action log; editable, exportable, multi-language. ServiceNow, Salesforce, API. Session Analytics dashboards. | The most mature native implementation. **Rule-based anonymisation on the client before upload**, passwords never recorded, a custom model catches tokens and out-of-office dates; Azure OpenAI in declared regions, no training. |
| **BeyondTrust Remote Support 26.2.1** | Shipped, cloud only | Auto summary at session end: case summary, step-by-step resolution, system info and tags, highlights. | No privacy detail in the release notes. |
| **Zoho Assist "Session Highlights"** (v16.5, Aug 2026) | Shipped | Key actions, issue category, apps used, summary; filter by tech, category, tag; export. Zia session audit reports since 2024. | |
| **LogMeIn Resolve** (ex-GoTo Resolve) | Shipped | AI session notes from recorded sessions, convertible into KB articles with auto label. 2026 added a Resolution Agent (agentic fixes with one-click approval). | |
| **ConnectWise ScreenConnect** | **Not shipped** | The AI session-summary request (citing Atera) sits "Pending Review" in the feature portal. Release notes through 2026.6 (2026-08-19) show no session AI. The PSA integration saves chat, notes, timestamps and machine config to the ticket. | **The biggest open window and the biggest future risk.** Re-check every release. |
| Splashtop | None | H1 2026 AI work is patch summaries and a codec. | |
| AnyDesk, NinjaOne, Intune Remote Help | None | NinjaOne has session recording only. Intune Remote Help stores no recordings or screen content. | |
| SuperOps | Wishlist only | Remote session summaries are a community request; Monica AI does ticket summaries. | |

### 2.3 PSA and ITSM text-thread AI (not session capture)

ConnectWise Sidekick, Kaseya Cooper Copilot (Autotask/BMS), Datto RMM Activity Log Insights
(natural-language questions over up to 10,000 activities including remote-control sessions),
HaloPSA native triage and summaries, Syncro AI Ticket Summary, Freshservice Freddy Copilot
($99/agent/month), ServiceNow Now Assist Resolution Notes (auto-triggers on Resolve), Jira Service
Management Rovo, Thread Magic (chat-native; on resolve writes a recap and logs time). Pia, Rallied,
Mizo, CloudRadial, Rewst, MSPbots and DeskDay do triage, automation or autonomous resolution.
Hudu has no native AI documentation feature; third parties bolt it on.

None of these see the screen. They summarise the ticket thread.

### 2.4 Gaps nobody fills

1. **Tool-agnostic capture.** Every native summary is locked to its own client. Nobody covers plain
   RDP (`mstsc`) at all, and nobody detects "a remote-session window is focused" across
   ScreenConnect, RDP, Splashtop, TeamViewer and AnyDesk.
2. **Local-first processing.** Only TeamViewer claims client-side anonymisation. Nobody transcribes
   the microphone locally. Cyft and Vight process audio and screens in the cloud.
3. **Evidence attached to the note.** Click-triggered screenshots as attachments to the ticket note
   exist nowhere; the only click capture in the space (IT Glue) targets SOPs, in a browser.
4. **One review, three destinations.** Nobody writes the ticket note, the time entry and the Hudu
   article from the same session review. Vight lists Hudu, but for KB drafts.
5. **A fixed note shape.** Problem → Steps → Result → Follow-ups. Incumbents produce free text or
   "itemised actions".

These five are ScreenTail's wedge and they are all already design decisions in the Build Plan.
The scan confirms them; it does not change them.

---

## 3. Positioning: the technician's black box

"Auto-documentation" is what every incumbent will call its feature. A sharper frame:

> ScreenTail is the technician's black box. It records what happened in any remote session, on
> the device, and gives that memory three jobs: the ticket note and time entry **now**, the
> knowledge-base article **when the fix repeats**, and tamper-evident proof **when a client asks
> what you did to their server**.

No competitor does all three, and none does any of them across tools. The audit hash chain
(ST-045) is already the foundation of the third job.

---

## 4. Feature ideas

Ranked by value against cost for a solo builder. Ticket numbers mark where a foundation already
exists. **None of these is a ticket yet.** Adding one means writing it into the backlog with
acceptance criteria; the Guide's rule against expanding scope inside a ticket still applies.

### 4.1 Recommended for v1 or v1.1

| # | Idea | Builds on | Why | Borrowed from |
|---|---|---|---|---|
| 1 | **Errors that appear without a click.** Detect a small centred dialog appearing in the remote-canvas region, capture it, OCR it, and extract error codes and event IDs as structured fields. Tag the frame as an error. | ST-026 already catches the frame; ST-041 already OCRs it | Click-triggered capture misses exactly the errors a technician reads without clicking. Structured error codes make sessions searchable across clients ("0x80070005 seen at three sites this month"), which is the start of the tribal-knowledge story. | Anthropic computer-use guidance; Microsoft Clarity smart events |
| 2 | **Keep the dead ends.** Have the note prompt render "tried X → error → did Y" instead of a flat step list. | ST-061 | What did not work is the most valuable part of a KB article and the first thing an escalation reader wants. | Supademo branching |
| 3 | **Named markers as section anchors.** Extend the mark-moment hotkey so the technician can say two words — "root cause", "waiting on vendor", "workaround" — and the draft uses the marker as the interpretive cue for that section. | ST-029, ST-028 | A technician will never type during a fix but will hit a key and say two words. This is Granola's core trick. | Granola |
| 4 | **Every sentence links to its frame.** Make hover-to-see-the-evidence the primary trust mechanism in Review. | ST-061 already rejects dangling frame references; ST-074, ST-075 | Verifying a claim in one hover beats reading the whole note. Every adjacent category converged on this. | LogRocket Galileo, Fireflies, Teams recap |
| 5 | **Dead-time accounting.** Detect reboots (the remote window vanishes and reconnects) and long idle stretches. Show the time entry as "45 min, of which 20 waiting on Windows Update" with the idle line toggleable. | ST-066, ST-022 | Unbilled time is the ROI story Vight sells on. Idle subtraction the technician can see and override is more trustworthy than a silent rounding rule. | xop.ai, Vight |
| 6 | **Three audiences from one session.** Internal note (default), customer-facing plain-English summary, KB draft. | ST-061, ST-096 | Owners show the customer-facing one to clients. Cyft ships this; it is a prompt variant, not new capture. | Cyft, Kaseya Smart Writing Assistant |
| 7 | **Commands block.** When the canvas shows PowerShell or CMD, OCR the command lines into their own section of the note. | ST-041 | The exact command is the most reused line in any note. | Keeper Connection Manager typescript recording |
| 8 | **Signed evidence export.** A signed PDF or JSON export of one session: frames, note revisions, audit chain. | ST-045 | For disputes, client audits and cyber-insurance questionnaires. Cheap, and nobody in the MSP space sells it. | Teleport, CyberArk PSM |

### 4.2 Good, later

| # | Idea | Builds on | Why | Borrowed from |
|---|---|---|---|---|
| 9 | **Pre-session recall.** When a session starts and the ticket is inferred, show a one-line card: last visit to this company, same error seen before, linked KB article. | ST-077, ST-079, ST-122 | Uses the store that already exists. | Cyft similar-ticket correlation |
| 10 | **Delete the last five minutes.** A panic hotkey for "I just saw their payroll screen", plus a per-client capture policy (a healthcare client can be text-only or no-screenshots). | ST-029, ST-044, ST-047 | Recall's lesson: filters miss; give the user a one-key answer. Per-client policy is what regulated-sector MSPs will ask for. | Microsoft Recall time-range delete |
| 11 | **Stale-article flag.** If the session's menu path diverges from the linked KB article, mark the article as possibly outdated. | ST-122 | The technician just proved the article is wrong; vendor UIs change. | xop.ai |
| 12 | **Automation opportunity report.** "This fix happened 14 times at 6 clients this month" as a dashboard signal to write an RMM script. | ST-120, ST-122 | Owners buy on this. It is the inverse of duplicate detection. | xop.ai third-occurrence drafting |
| 13 | **Dead-click and hang signals.** Click timing plus frame diffs in the canvas region: "clicked, nothing changed for 8 seconds". | ST-024, ST-026 | Usually means the endpoint hung, which belongs in the note. | FullStory frustration signals |
| 14 | **Zoom crop per click.** Store a full-resolution crop around each click alongside the frame. | ST-025 | Makes the drafting model read small dialog text and shrinks the PII surface sent anywhere. | Anthropic computer-use `zoom` |
| 15 | **Second redaction pass at publish.** A cheaper OCR pass at Review that flags likely-missed secrets and requires an explicit "I checked" on flagged frames. | ST-042, ST-078 | Publish is the last gate before Hudu, where many people can see it. Recall's filter missed SSNs and card numbers with the filter on. | Tom's Hardware Recall test |
| 16 | **Ask the session.** A local question box over one session ("what was the error code?") whose answers cite the frame or transcript second. | ST-076, ST-065 | Faster than scrubbing. | Fireflies AskFred, Datto Activity Log Insights |

### 4.3 Considered and held back

- **Pixel-level element naming** (OmniParser-style detector on the canvas so a step reads "clicked
  OK in the Windows Update dialog" rather than coordinates). Heavy for a solo builder; the vision
  model with a zoom crop (#14) gets most of the way there.
- **Multi-session attribution** for technicians juggling several remote windows. Real, and a
  privacy concern (cross-client contamination), but check whether the state machine (ST-020)
  allows concurrent sessions at all before designing for it.
- **Sentiment and coaching scores** (Vight, TeamViewer). Manager-facing, needs end-user audio, and
  reads as surveillance to technicians. Not before v1.2's consent workflow, if ever.
- **Autonomous remediation** (LogMeIn Resolution Agent, Vight Level 2). A scope non-goal.

### 4.4 Smaller borrowings

- Tray badge and a logged reason for every skipped capture ("Paused: password field"). Recall
  shipped this after its backlash; ST-071 and ST-045 already have the pieces.
- Default exclusions should include password managers, MFA apps and personal browsers (ST-043).
  Critics of Recall called out that Signal and conferencing apps were not excluded by default.
- Field-level PSA writes (resolution, time-entry notes, billable flag, work type) rather than one
  pasted note (ST-093, ST-094). Billing reports break on blobs.
- Follow-ups as a dedicated extraction that requires an owner and a trigger, not a summary
  by-product (ST-061). Gong claims twice the accuracy for next-steps this way.
- Templates per ConnectWise board or ticket type with different sections and time-entry defaults
  (FR-15 already; this is the concrete shape).
- Admin/tech visibility split for summaries and an account toggle that also purges history (Atera).
- Store window titles and OCR text per frame as a searchable index separate from images
  (CyberArk indexes RDP recordings this way). ST-041 already stores the OCR result; expose it.

---

## 5. Things a reader should understand

- **The remote canvas is a bitmap.** Inside RDP or ScreenConnect there is no accessibility tree,
  so everything comes from OCR, click timing and the technician's voice. That is why narration is
  the strongest signal and why the design leans on it (Scope §7.1).
- **Redaction is the product, not a feature.** Microsoft Recall's failure was default exclusions
  and silent misses, not encryption. `docs/review/weaknesses.md` found the same class of problem
  here: a frame the OCR engine reads nothing from is stored as redacted. That fix gates everything.
- **One model call per session** is a cost and a privacy decision together: under ten cents per
  session, and the backend holds frames in memory for that one request only (INV-7).
- **Consent law** is why v1 captures the technician's microphone only. Twelve US states require
  all-party consent for the end user's audio (Scope §7.2).

---

## 6. Open decision

Of the three jobs in §3 — faster notes for technicians, knowledge that compounds, proof for
disputes and audits — which does the pilot MSP care about most? The answer decides which of the
sixteen ideas become tickets first. ST-110's baseline interviews are the place to ask.

---

## Sources

Competitor and adjacent-product claims, as read on 2026-09-15.

- Cyft: https://cyft.ai/
- Vight: https://vight.ai/ ; Level 2 announcement https://www.globenewswire.com/news-release/2026/09/03/3356178/0/en/
- IT Glue Smart SOP Generator: https://help.itglue.kaseya.com/help/Content/2-using/documentation-guide/sop-generator.htm
- Lexful: https://lexful.ai/
- Atera AI Copilot: https://support.atera.com/hc/en-us/articles/9702543154844-AI-Copilot-s-capabilities
- TeamViewer Session Insights: https://www.teamviewer.com/en/global/support/knowledge-base/teamviewer-remote/remote-control/generate-session-summaries-with-session-insights/ ; security https://www.teamviewer.com/en/products/add-ons/ai/security/
- BeyondTrust 26.2.1: https://docs.beyondtrust.com/rs/changelog/remote-support-26-2-1-release-notes
- Zoho Assist: https://www.zoho.com/assist/whatsnew.html
- LogMeIn Resolve: https://support.logmein.com/resolve/help/working-with-session-notes-20 ; https://www.goto.com/blog/2026-resolve-rescue-ai-features
- ScreenConnect feature request: https://screenconnect.product.connectwise.com/communities/1/topics/4852-screenconnect-ai ; release notes https://docs.connectwise.com/ScreenConnect_Documentation/ScreenConnect_release_notes
- Splashtop H1 2026: https://www.splashtop.com/blog/splashtop-product-recap-h1-2026
- Kaseya Autotask 2026.3: https://www.kaseya.com/blog/autotask-2026-3-release/ ; Datto Activity Log Insights https://rmm.datto.com/help/en/Content/3NEWUI/Analytics/ActivityLogInsights.htm
- ServiceNow resolution notes: https://www.servicenow.com/docs/bundle/xanadu-intelligent-experiences/page/administer/now-assist-platform/concept/now-assist-rn-summarization.html
- Thread Magic: https://www.getthread.com/magic
- xop.ai Hudu and ConnectWise integrations: https://xop.ai/integrations/hudu ; https://xop.ai/integrations/connectwise-manage
- Hudu AI: https://www.hudu.com/ai
- Granola templates: https://www.granola.ai/blog/meeting-note-templates-by-type
- Gong Call Spotlight: https://www.gong.io/call-spotlight
- Fireflies AskFred: https://fireflies.ai/blog/fireflies-ai-vs-microsoft-365-copilot/
- LogRocket Galileo Highlights: https://docs.logrocket.com/docs/galileo-highlights
- FullStory frustration signals: https://help.fullstory.com/hc/en-us/articles/360020624154
- Microsoft Clarity smart events: https://learn.microsoft.com/en-us/clarity/setup-and-installation/smart-events
- Microsoft Recall filtering: https://support.microsoft.com/en-us/windows/ai/ai-features/filtering-apps-websites-and-sensitive-information-in-recall ; Recall filter misses https://www.tomshardware.com/software/windows/microsoft-recall-screenshots-credit-cards-and-social-security-numbers-even-with-the-sensitive-information-filter-enabled ; AdGuard critique https://adguard.com/en/blog/microsoft-recall-privacy-threat-adguard.html
- OpenAI Operator takeover mode: https://openai.com/index/introducing-operator/
- Screenpipe: https://docs.screenpipe.com/home
- Anthropic computer-use tool: https://platform.claude.com/docs/en/agents-and-tools/tool-use/computer-use-tool
- Microsoft OmniParser: https://github.com/microsoft/OmniParser
- CyberArk PSM recordings: https://docs.cyberark.com/pam-self-hosted/latest/en/content/pasimp/configure-recordings-in-psm.htm
- Keeper Connection Manager session recording: https://docs.keeper.io/keeper-connection-manager/using-keeper-connection-manager/session-recording
- Teleport session recording: https://goteleport.com/docs/reference/architecture/session-recording/
- Supademo branching: https://supademo.com/tools/interactive-demo-builder ; Folge https://folge.me/
- Loom AI workflows: https://support.atlassian.com/loom/docs/use-ai-workflows/
