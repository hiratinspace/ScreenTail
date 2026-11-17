# Security hardening, November 2026

**Written:** 2026-11-16, of `main` at `bfb0d4f8` (#333).
**Scope:** the security review of 2026-11-15 and the fixes merged on 2026-11-16.

## Summary

On 2026-11-15 an internal security review read the whole of `main`: the backend, the web admin, the
Windows client, the installer, the update path and CI. It found **no Critical issue, no authentication
bypass and no cross-tenant access**. Every finding was worked through in seven pull requests, #321 to
#327, all merged on 2026-11-16, with four follow-ups, #330 to #333, the same day. One item was kept as a
deliberate design choice, and the reasons are written down in `docs/integrations/connectwise.md` ("What
the deployment supplies"). The review's own text stays private.

This page says what is true now, area by area. The design behind it is in `threat-model.md`,
`data-flow.md` and `questionnaire.md` beside this file. The pack has still not been read by an MSP
security lead (ST-114).

## What is true now

**Integration credentials are the owner's.** Only the tenant's owner connects, replaces or disconnects
ConnectWise and Hudu, from the web admin; a technician's Settings show the connection read-only and can
still test it. No route returns any part of a secret, the owner's included. A stored secret is bound to
its tenant, provider and site, so it does not open anywhere else. Every credential change, and every
company mapping a person makes, is recorded and shown to the owner on the Integrations page with who
made it.

**Publish and provider inputs are bounded.** Publishing is limited per device and per tenant before the
request body is read, and every part of a note has a size limit. Provider names, credentials, site
addresses and request bodies on the integration routes are capped, and so is the company list read from
Hudu. A note's "Reviewed by" line always names the signed-in technician. The device sign-in routes read
small bodies only and refuse a token that is not in the shape ScreenTrail issues.

**A locked local-only policy holds without trusting the device.** When an admin locks local-only, the
backend itself refuses to draft for that tenant, and the client drafts the session on the device
instead. An installed client reads its backend address from a machine-wide setting that only an
administrator can write (`BACKENDURL` at install), not from the technician's own environment. Under
local-only the device's background sign-in renewal sends nothing.

**The pipe between the UI and the capture service is stricter.** In a signed build each side accepts the
other only when it is signed by ScreenTrail's publisher and runs from the same install folder. The
service claims its pipe name with the first instance it creates, so it never shares the name with a
pipe it did not make. If the name is taken, the UI says the capture service is not running and capture
stays suppressed until a verified UI is connected.

**Capture privacy.** A click where no screenshot may be taken (an excluded app, an out-of-scope window,
a window that is not in front) is kept only as a counted event, with no screen position. Exclusions
written as web addresses are matched against the page title the browser shows, and Settings says so. A
window drawn see-through, or colour-keyed when it is not a known remote tool, is not photographed, so
what lies behind it cannot reach a frame. A window whose title just changed is judged on the new title
before the next frame, and a title not yet confirmed can only make the decision stricter.

**Each update channel has its own signing key.** A key is trusted for pilot or for stable, never both,
and the client refuses a manifest signed with the other channel's key. A build signed without Artifact
Signing's identity check accepts an update only from the same signing key. Uninstall and repair refuse
any install folder other than ScreenTrail's own under Program Files. Updates themselves stay switched
off until the owner sets them up (ST-113).

**CI and the self-hosted runner are hardened.** The laptop jobs start PowerShell without user profiles
and use actions pinned to exact commits. A laptop job that was skipped no longer counts as evidence, and
a pull request from a fork never gets hardware evidence. The end-to-end test backend no longer reads the
demo's stored secrets, and the backend's container image cannot carry local settings or secret files.
Secret scanning, push protection and Dependabot security updates are on, and the three repository
settings that keep a stranger's pull request off the laptop are unchanged.

**Dependencies are up to date.** The 83 Dependabot alerts opened on 2026-11-16, all in development and
build tools or the fixture renderer's image library, none in what the product runs, were closed the same
day by #321, #328 and #329. None is open, and the .NET projects report no vulnerable package.

**PSA company names are checked against the PSA.** A company mapping is saved only for a company the
tenant's PSA has, looked up live, and under the PSA's own spelling. If the PSA cannot answer, nothing is
saved. The ConnectWise form of the lookup is still to be seen working against a real ConnectWise
sandbox.

## Still owed

- **On the first deployment:** run the vault upgrade, then set `Vault:AllowLegacyRows=false` once it
  reports no legacy rows (`key-rotation.md`).
- **On the laptop runner:** a drafting key with its own spend cap, a database credential that reaches
  only the test databases, and the runner under a standard Windows account that holds none of the demo's
  secrets (`docs/dev/windows-test-loop.md`).
- **Known limits** that remain are listed in `threat-model.md`, "What is not mitigated, and why", and in
  ADR-0003.
