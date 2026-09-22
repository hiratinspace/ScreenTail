# ScreenTail documentation — where to look

Three kinds of document live in this repository. Knowing which kind you are reading tells you how much
to trust it and where to change it.

## Intent — what we are building (`Build Plan/`)

| File | Read it for | Change it when |
|---|---|---|
| `00-Build-Agent-Guide.md` | Invariants INV-1…12, conventions, how a ticket is executed, milestone gates | A convention or invariant changes (rare; needs an ADR if architectural) |
| `01-UX-Design-Spec.md` | Everything a user sees: tokens, screens, states, copy, shortcuts. Authoritative. | A UI ticket needs a deviation — amend §9 first, then build |
| `02-Backlog-v0.4.md` | The tickets. Each has a **Status** line (the source of truth for its state), dependencies, acceptance criteria and an agent brief. Part C is the ordered plan; Part D logs amendments. | In the PR that changes a ticket's state or dependencies |
| `ScreenTail-Project-Scope.md` | The original goals, success metrics, non-goals and risks (v0.1, 2026-09-10). Superseded as the working spec; kept unedited. | Never — write a new scope version instead |

## Record — what we decided and where we are (`docs/`)

| File | Read it for |
|---|---|
| `STATUS.md` | One page: what exists, what's decided, what's open, what's next. A summary of the backlog's status lines, refreshed after each stretch. Last refreshed 2026-09-22 |
| `ipc-contract.md` | The service–UI pipe contract, versioned. Kept at the root because three source files and the backlog cite it by this path |
| `adr/000N-*.md` | Architecture decisions. All six are Accepted. `adr/evidence/` holds the measurements they rest on, with its own README |
| `review/weaknesses.md` | The 2026-09-15 adversarial review, closed 2026-09-22: every finding says what fixed it, and the one still open says where it is tracked. Kept because code comments cite its numbers |
| `security/threat-model.md`, `data-flow.md`, `questionnaire.md` | The privacy pack (ST-114). Brought into line with the code on 2026-09-22. Items marked ⚠ are not yet built; the pack has not had a security-lead review and says so |
| `legal/consent-guidance.md` | Technician scripts and the consent-law summary. Not reviewed by a lawyer |
| `product/` | Market scans and idea lists. Reference only; nothing here is a ticket until it is in the backlog |
| `dev/first-draft-end-to-end.md` | Getting one real session to produce one real note: the M1 step list, everything on the laptop |
| `dev/windows-test-loop.md` | How Windows-bound work is verified from a Mac dev box, and what keeps strangers' code off the laptop |

## Evidence — what was measured (`docs/adr/evidence/`, `docs/ux/`, `research/`, `shared/`)

| Where | What |
|---|---|
| `adr/evidence/0001/` | ST-001 spike runs: hosted runner, laptop, load, legibility. `README.md` there says which folder is which run |
| `ux/wireframes/` | ST-014 low-fi wireframes for S1–S8 with PNG exports; the technician-session sheet is still empty |
| `ux/review-hifi/` | ST-015 hi-fi Review screen, 8 states × 2 themes, contrast report. CI's contrast check reads from here |
| `research/fixtures/` | Hand-drawn session bundles (ST-006) and the empty audio folder that needs a human recording (ST-027) |
| `research/prompts/` | The note prompt, its post-condition checks and the hardening cases both the Python and the C# validators run |
| `research/eval/` | The WER script; the redaction and draft-quality harnesses arrive with ST-030 and ST-062 |
| `shared/contracts/` | The summarize-request wire contract, tested from both the client and the backend |

## Conventions

- A document that describes something not yet built says so in the sentence that describes it.
- Dates are absolute. A snapshot document carries its date at the top.
- Anything that changes architecture gets an ADR before the code.
- A file ending `.local.md` is a working note and is never committed; that is where unfixed security
  findings live until they are fixed.
