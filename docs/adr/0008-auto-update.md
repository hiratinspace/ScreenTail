# ADR-0008: The auto-update channel

- **Status:** **Proposed 2026-10-29** (ST-113). Built and tested on the Mac suite and in CI; nothing is
  switched on until the owner creates the signing key and the hosting (below, "Owner steps"). The owner
  decides the choices listed under "Decisions for the owner" before this leaves Proposed.
- **Date:** 2026-10-29
- **Ticket:** ST-113 (builds on ST-112 and ADR-0007's seam; ST-007 hosts the feed)
- **Deciders:** project owner; build agent

## Context

ST-113 asks for signed updates, a stable and a beta (here: pilot) channel, background download, a
prompt only when idle, never during a session, a refused signature logged, and the channel selectable in
Settings. Five facts shape it:

1. **The install is per machine** (ADR-0007): `C:\Program Files\ScreenTail`, which only administrators
   can change. Neither ScreenTail process has administrator rights, and an update needs them.
2. **Both processes run as the technician** (ADR-0003). There is no privileged ScreenTail process, and
   adding one is a standing attack surface on every technician's machine.
3. **MSPs already push software with Intune and RMM agents**, as the system account, silently. Many will
   prefer that to any self-update.
4. **Local-only is a promise about requests** (INV-8), and admin policy is enforced on the client
   (INV-11). An update check is a request nobody asked for at that moment.
5. **Artifact Signing rotates its leaf certificate every few days** (ADR-0007), so "signed by the same
   certificate as the running build" is false for every update.

## Options compared for applying an update

| | **A. Technician-launched, Windows Installer elevates (chosen)** | B. MSP's own tool only | C. Scheduled task as SYSTEM | D. Update service as SYSTEM |
|---|---|---|---|---|
| New privileged code | None | None | An elevated updater, run on a trigger | A service running all day |
| Needs an administrator at the moment | Yes, at the UAC prompt (or the MSP pushes instead) | No | No | No |
| Unattended fleet update | No; that is B | Yes | Yes | Yes |
| What an attacker with the technician's rights gains | Nothing new: they cannot write Program Files, and the package is re-verified through a write-locked handle immediately before it is started | Nothing | A path from user-writable state to SYSTEM if any check is wrong | The same, all day, plus whatever the service listens on |
| Fits "never during a session" | Yes: the service holds starts and Install is refused while recording | Up to the MSP's schedule (ADR-0007: upgrades end running copies) | Needs a session check across processes | Needs a session check across processes |

**Chosen: A, with B always available.** The service downloads and verifies in the background; Settings
offers "Install update" only when an update is ready and no session is recording; pressing it re-verifies
the package and starts Windows Installer, which asks for an administrator's approval for the
per-machine package (UAC). Nothing new runs with rights the technician does not have. A technician who
cannot approve gets the update when their MSP pushes it, exactly as today (an admin can also turn update
checks off, so the card says so instead of offering something the MSP does not want).

C and D are rejected for now: either is a process that turns state in a user-writable profile into
actions as SYSTEM, which is the class of bug that matters most here, and the ticket does not need
unattended updates. If pilots show technicians routinely cannot approve UAC and MSPs will not push, C is
the follow-up to consider, with its own ADR and threat model.

## Decision

### The manifest

Served as a static file, `<feed><channel>/win-x64.json`. On the wire an envelope:

```json
{ "format": "screentail-update-manifest-v1", "key": "<key id>",
  "payload": "<base64 of the signed bytes>", "signature": "<base64, r then s, 64 bytes>" }
```

The signed bytes are UTF-8 JSON: `format` (`screentail-update-v1`), `channel`, `architecture` (`x64`),
`version`, `minimumVersion` (the oldest installed version that may update straight to this one; older
installations are told to reinstall), `package` (`url`, `sha256`, `size`) and `published`. The signature
is ECDSA P-256 with SHA-256 over exactly those bytes, so there is no canonical form to get wrong.

**The client refuses, and logs with a fixed sentence of its own (never the manifest's text), anything
that fails any of these, in this order:** the envelope's shape and size (64 KB at most); a key id it
knows; the signature, with a public key compiled into the client; then the payload's shape, its channel,
its architecture, a SHA-256 of 64 lowercase hex characters, a size from 1 byte to 512 MB, a package URL
that is HTTPS on the default port with no user information on one of the compiled-in package hosts, a
version not older than the installed one (a downgrade), not older than the newest this installation has
already verified on that channel (a replayed manifest), and an installed version not older than the
minimum. The same version is "up to date". Ed25519 was considered; Key Vault does not offer it and .NET
has no in-box implementation, so P-256 is the one curve both ends have without a new dependency.

### Trust, compiled in

`shared/release/update-trust.json` holds the feed URL, the exact package host names (no wildcards) and
the public keys. ScreenTail.Core embeds it, so nothing on the machine can redirect the feed or add a key;
CI's signing step reads the same file and checks every manifest against it before uploading. **It ships
empty**: a build without a feed or a key makes no update request at all and says so in Settings.

### The request, and the egress guard

A new egress purpose, `UpdateCheck`, allowed only to the trust file's hosts. The check is one GET with
no credential, cookie, device, tenant or version; the channel and the architecture are in the path. The
download is a second GET under the same purpose; a redirect is followed only onto another update host,
and only `Range` travels with it (EgressGuard's existing rules). The update client has a guard of its own
over the same policy (`EgressGuard.ForUpdates`), whose handler keeps no cookies, so nothing the feed sets
comes back to it.

**Under local-only no update request is made.** The one exception is the admin's, and only to a
local-only the admin locked on: with `localOnlyLocked` and `localOnly` both set in the tenant's policy,
`updateChecksUnderLocalOnly` lets update checks (and nothing else) through. **A local-only the technician
turned on themselves lets no update request out, whatever `updateChecksUnderLocalOnly` says**, because
that local-only is the technician's promise that nothing leaves; the policy applier passes the exception
only with the admin's lock, and the egress decision itself requires both (2026-10-29 review). No check
runs before this run has fetched the tenant's policy once, because until then the policy in force is a
cached file the technician can edit (the outbox's rule).

**Revocation is not checked**, deliberately: `WinVerifyTrust` is called with revocation checks off, as
the peer checks are. Checking the whole chain's revocation makes Windows fetch CRLs and OCSP answers from
the certificate authorities' hosts, which are not on the allowlist and would be egress the client did not
decide (INV-8). The timestamp, the identity comparison and the signed manifest's SHA-256 are the checks.

### Admin policy (INV-11)

Four optional fields on `shared/contracts/tenant-policy.v1.json`, defaults in brackets: `updateChannel`
(`stable`), `updateChannelLocked` (false: the technician chooses in Settings), `updateCheckHours` (24;
0 turns checks off; at most 168), `updateChecksUnderLocalOnly` (false). Optional on the client, so an
older backend still syncs; the backend stores and serves them, and a migration gives existing policies
the defaults rather than zeros. The technician's channel lives in `updates.json` beside the settings, not
in the Privacy document, which is saved whole.

### Download and install

- **Background, and only when idle:** a verified offer downloads only while no session is recording
  (recording, paused, suppressed or finalizing), into `%LOCALAPPDATA%\ScreenTail\Updates`. The folder
  is prepared before anything is read from or written in it, and every time: inheritance cut and access
  for the technician, the system account and administrators only, applied to a folder that already
  existed too, and refused if it is a link. A session that starts during a download stops it after the
  chunk in hand; what arrived is kept, and the next check (woken when the session ends) continues it with
  a `Range` request, or starts again if the feed ignores the range. The bytes read are capped at the
  manifest's size whether or not the feed sends a length. The file is kept only if its size and SHA-256
  over every byte match the manifest; anything else is deleted and logged. Older packages are removed
  after a download, and at every start those no newer than the installed version.
- **A channel that is behind:** a validly signed manifest older than the installed version (a
  technician who moved from pilot to stable while stable is behind) is "up to date, newer than that
  channel", not a refusal logged on every check. An older manifest than one already seen on that channel
  is still refused.
- **The prompt only when idle:** the tray announces a ready update once per version, never during a
  session (Spec §4's rule, `UpdateNotices`); Settings shows Install enabled only when ready and idle.
- **Never during a session:** Install asks the session machine for a hold on new sessions
  (`SessionMachine.HoldStartsAsync`). It is granted only when no session is under way, under the same
  transition lock every start takes, and while it is held every start path (automatic, the hotkey, the
  pipe) is refused, with the pipe saying an update is being installed. It is released if any check fails
  or when Windows Installer exits, and then the window in front is decided again, so a tool that took
  focus meanwhile gets its session without the technician clicking away and back. There is no timeout
  after which sessions may start while Windows Installer runs, and the pill has no "installing" state
  (both are decisions for the owner, below). Install's checks take the update check's own lock, so a
  check that finds a new release cannot replace the package being verified, and a refusal deletes only
  the package that was checked.
- **Re-verified immediately before it is launched:** the package is opened for reading with sharing that
  denies writing, renaming and deleting (the Windows suite shows the file, and its folder, cannot be
  changed while the handle is open); its size and SHA-256 are read through that handle and compared with
  the verified manifest; `WinVerifyTrust` checks its Authenticode signature through the same handle; and
  its publisher must be the running service's, **by identity, not by leaf thumbprint**: the signing
  certificate's subject, plus Artifact Signing's identity-validation EKU (`1.3.6.1.4.1.311.97.*`) when
  the running build carries one, with a timestamp required. Only then is `C:\Windows\System32\msiexec.exe`
  started by full path, without a shell, as `/i <package> /passive /norestart`; Windows Installer raises
  the UAC prompt for the per-machine package. The handle is held until it exits.
- **A build nobody signed never updates itself:** there is no publisher to compare with.
- **After an update** the MSI ends running copies (ADR-0007) and the service starts again at the next
  sign-in; the card says so before Install is pressed.

### Hosting

A static HTTPS location (ST-007's storage), not a backend endpoint: the manifest is public, signed and
the same for every tenant, so nothing about it needs a tenant, a token or the backend's availability, and
a compromised host can only withhold updates, not forge one. The owner may serve it from the backend
later; it would then serve the signed file and nothing else.

### Signing in CI

**Builds are signed for pilot only.** One step in the existing signing job (`installer-sign`), after
every Authenticode signature has been checked: `installer/update-manifest.ps1 -Channel pilot` builds the
pilot manifest for the signed MSI, asks Azure Key Vault to sign its digest with the release key (ES256)
through the job's own OIDC token and the same federated app registration, verifies the signature against
the trust file's keys on Windows PowerShell 5.1, and writes it. The channel is a required parameter with
no default and no "all", and a build may not name stable. The private key never leaves Key Vault. Until
`SCREENTAIL_UPDATE_KEY` and `SCREENTAIL_UPDATE_BASE` are set on the `signing` environment it signs
nothing, with a notice, like `sign.ps1` without a certificate; the unsigned job runs the same step on
every pull request. The pilot manifest is its own artifact, kept 90 days so a later promotion can read it.

**Stable is signed only by promotion** (`.github/workflows/promote-stable.yml`, its own file so ci.yml's
triggers are unchanged): started by hand on main with the id of the ci run whose pilot release is
promoted and its version, on the hosted runner, always in the `release-stable` environment. It first asks
GitHub what that run is and stops unless it is this repository's ci.yml, started by a push, on main, and
successful (`installer/check-pilot-run.ps1`, with the job's read-only token). Then it downloads
that run's pilot manifest (a JSON file; nothing from the earlier run is executed) and the script signs,
for stable, exactly what it describes, after checking that it verifies with the client's own keys, says
pilot and carries the version typed. **It fails closed**: the script refuses unless
`SCREENTAIL_STABLE_RELEASE` is `true`, a variable that exists only on the owner's protected environment
(an environment GitHub creates on first use, without reviewers, does not have it), and Azure issues the
vault token only to an environment it was told to trust. The stable manifest is uploaded for a day, for
the owner to publish; an ordinary CI run never produces or uploads one. A pilot manifest copied to the
stable path is refused by every stable device, because the channel is inside the signed bytes.

`scripts/ci/check_installer_jobs.py` holds the signing step's exact command, settings and place, the
uploads, the promotion workflow's whole shape (trigger and inputs, condition, environment, permissions,
runner, steps, the gate variable coming from the environment rather than the file), the script's own
checks, and that no other workflow asks for a token or uploads either manifest. Nothing that protects the
self-hosted runner changed: every installer job and the promotion run on the hosted runner, and no
trigger, permission, filter, gate or skip budget of an existing job moved.

## Decisions for the owner

1. **The apply model (A above).** Alternatives: B only (no Install button; checks still say what is
   available), or a later C with its own ADR.
2. **Local-only default:** no update checks under local-only unless the admin allows it (fail closed).
3. **Defaults:** stable channel, chosen by the technician unless the admin locks it; a check every 24
   hours; 0 hours means off.
4. **Key custody:** an Azure Key Vault ES256 key (Premium, HSM-backed, recommended), signed only from the
   `signing` environment through OIDC, with a role that allows `sign` and nothing else.
5. **Hosting:** a static HTTPS location under ST-007, with its host names compiled in.
6. **Promotion:** every signed build on main is signed for pilot only; stable is signed only by
   `promote-stable.yml` in the `release-stable` environment, behind the owner's required reviewers.
   Publishing either manifest to the feed is a manual owner step for now.
7. **`minimumVersion`** is 0.1.0 for every release until a store migration needs a stepping stone.
8. **Freshness:** a replayed older manifest is refused, but a feed that keeps serving the same old,
   validly signed manifest is not detected (no expiry field). An expiry would need a re-signing job on a
   schedule; the owner decides whether that is worth it.
9. **The `release-stable` environment** (an owner setting this branch cannot create): its required
   reviewers (the owner by default), its main-only branch rule, and Azure trusting it for the vault key.
10. **No timeout on the install's hold.** While Windows Installer runs no session starts, however long
    the technician leaves the UAC prompt open. A timeout would let a session start that Windows Installer
    then ends; the alternative is the owner's to choose.
11. **No "installing" state on the pill.** Between Install and the service ending, nothing on screen says
    an update is being installed except Windows' own prompt and progress. A pill state needs an IPC change
    and a UX decision; left for the owner.
12. **Revocation is not checked** (above): the owner may decide to allow the CA hosts for that one check.

## Owner steps

1. **Azure Key Vault:** create a vault (Premium for an HSM-backed key) and an EC P-256 key, purpose sign
   only. Give the existing signing app registration a role on that key that allows only
   `Microsoft.KeyVault/vaults/keys/sign/action` (a custom role; *Key Vault Crypto User* also works but
   allows more). Note the key identifier **with its version**.
2. **The public key:** export it as base64 DER SubjectPublicKeyInfo (`az keyvault key download --encoding
   DER` then base64) and add it to `shared/release/update-trust.json` with an id, in a reviewed pull
   request.
3. **Hosting (ST-007):** an HTTPS location for `<feed>pilot/win-x64.json`, `<feed>stable/win-x64.json` and
   `<base><version>/ScreenTail.msi`. Put the feed URL and the package host names in the trust file in the
   same pull request.
4. **GitHub, `signing` environment variables:** `SCREENTAIL_UPDATE_KEY` (the versioned key identifier)
   and `SCREENTAIL_UPDATE_BASE` (the packages' HTTPS folder, ending in a slash).
5. **GitHub, *Settings → Environments → New environment*: `release-stable`.** Mandatory before any
   promotion: *Deployment branches and tags* set to `main` only; *Required reviewers* (the owner);
   environment **variables** `SCREENTAIL_STABLE_RELEASE` = `true`, `SCREENTAIL_UPDATE_KEY`,
   `AZURE_TENANT_ID` and `AZURE_CLIENT_ID` (the same values as on `signing`). In Azure, add a second
   federated credential to the signing app registration for entity type *Environment*, name
   `release-stable` (subject `repo:hiratinspace/ScreenTail:environment:release-stable`). Until all of this
   exists the promotion fails closed.
6. **Each pilot release:** from the signing run's artifacts, upload `ScreenTail.msi` to `<base><version>/`
   and `pilot/win-x64.json` to the feed; check it first with `installer/update-manifest.ps1 -Verify`.
7. **Each promotion to stable:** *Actions → promote-stable → Run workflow* on main, with that ci run's id
   and the version; approve it as the environment's reviewer; upload the `stable/win-x64.json` it produces
   (artifact `screentail-stable-manifest`, kept one day) to the feed.

## Consequences

- Nothing changes on any machine until the trust file is filled in and a signed build ships it.
- A technician without administrator credentials cannot apply an update; the card says so before they
  press Install, and the MSP's own push keeps working as before.
- The IPC contract stays at version 4: the four update messages are new commands and a reply only ever sent to `get_update`, which the contract's own rule does not bump.
- The UAC prompt's wording, and that Windows Installer raises it under `/passive` when started without
  elevation, are checked on the laptop with the first signed build (`docs/dev/installer.md`).

## Alternatives considered

- **Comparing the leaf thumbprint.** Rejected: every Artifact Signing release has a new leaf (ADR-0007).
- **Signing the manifest with the Authenticode certificate.** Rejected: Artifact Signing signs files, not
  arbitrary bytes, and its certificates rotate; a separate long-lived key is simpler to pin.
- **A private key in a GitHub secret.** Rejected: a key that can be read by the job can be copied by it.
- **Downloading during a session.** Rejected: it shares a customer's support connection.
