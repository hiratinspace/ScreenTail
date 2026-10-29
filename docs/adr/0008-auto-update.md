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
and only `Range` travels with it (EgressGuard's existing rules). **Under local-only no update request is
made** unless the tenant's admin set `updateChecksUnderLocalOnly`; the technician's own local-only cannot
be overridden from the device. No check runs before this run has fetched the tenant's policy once,
because until then the policy in force is a cached file the technician can edit (the outbox's rule).

### Admin policy (INV-11)

Four optional fields on `shared/contracts/tenant-policy.v1.json`, defaults in brackets: `updateChannel`
(`stable`), `updateChannelLocked` (false: the technician chooses in Settings), `updateCheckHours` (24;
0 turns checks off; at most 168), `updateChecksUnderLocalOnly` (false). Optional on the client, so an
older backend still syncs; the backend stores and serves them, and a migration gives existing policies
the defaults rather than zeros. The technician's channel lives in `updates.json` beside the settings, not
in the Privacy document, which is saved whole.

### Download and install

- **Background, and only when idle:** a verified offer downloads only while no session is recording
  (recording, paused, suppressed or finalizing), into `%LOCALAPPDATA%\ScreenTail\Updates`, created with
  inheritance cut and access for the technician, the system account and administrators only, and refused
  if it is a link. The file is kept only if its size and SHA-256 match the manifest; anything else is
  deleted and logged. Older packages are removed.
- **The prompt only when idle:** the tray announces a ready update once per version, never during a
  session (Spec §4's rule, `UpdateNotices`); Settings shows Install enabled only when ready and idle.
- **Never during a session:** Install asks the session machine for a hold on new sessions
  (`SessionMachine.HoldStartsAsync`). It is granted only when no session is under way, under the same
  transition lock every start takes, and while it is held every start path (automatic, the hotkey, the
  pipe) is refused, with the pipe saying an update is being installed. It is released if any check fails
  or when Windows Installer exits.
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
  sign-in or when the technician opens ScreenTail; the card says so before Install is pressed.

### Hosting

A static HTTPS location (ST-007's storage), not a backend endpoint: the manifest is public, signed and
the same for every tenant, so nothing about it needs a tenant, a token or the backend's availability, and
a compromised host can only withhold updates, not forge one. The owner may serve it from the backend
later; it would then serve the signed file and nothing else.

### Signing in CI

One step in the existing signing job (`installer-sign`), after every Authenticode signature has been
checked: `installer/update-manifest.ps1` builds both channels' manifests for the signed MSI, asks Azure
Key Vault to sign each digest with the release key (ES256) through the job's own OIDC token and the same
federated app registration, verifies each signature against the trust file's keys on Windows PowerShell
5.1, and writes them. The private key never leaves Key Vault. Until `SCREENTAIL_UPDATE_KEY` and
`SCREENTAIL_UPDATE_BASE` are set on the `signing` environment it signs nothing, with a notice, like
`sign.ps1` without a certificate; the unsigned job runs the same step on every pull request. The
manifests are their own artifact, kept 90 days. `scripts/ci/check_installer_jobs.py` holds the step's
exact command, settings and place, the upload, and the script's own checks, and refuses the update
settings anywhere else. Nothing that protects the self-hosted runner changed: every installer job still
runs on the hosted runner.

## Decisions for the owner

1. **The apply model (A above).** Alternatives: B only (no Install button; checks still say what is
   available), or a later C with its own ADR.
2. **Local-only default:** no update checks under local-only unless the admin allows it (fail closed).
3. **Defaults:** stable channel, chosen by the technician unless the admin locks it; a check every 24
   hours; 0 hours means off.
4. **Key custody:** an Azure Key Vault ES256 key (Premium, HSM-backed, recommended), signed only from the
   `signing` environment through OIDC, with a role that allows `sign` and nothing else.
5. **Hosting:** a static HTTPS location under ST-007, with its host names compiled in.
6. **Promotion:** every signed build on main produces both a pilot and a stable manifest; publishing the
   pilot one releases to pilot, and publishing the stable one later promotes it. Publishing is a manual
   owner step for now.
7. **`minimumVersion`** is 0.1.0 for every release until a store migration needs a stepping stone.
8. **Freshness:** a replayed older manifest is refused, but a feed that keeps serving the same old,
   validly signed manifest is not detected (no expiry field). An expiry would need a re-signing job on a
   schedule; the owner decides whether that is worth it.

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
5. **Each release:** from the signing run's artifacts, upload `ScreenTail.msi` to `<base><version>/` and
   `pilot/win-x64.json` to the feed; check it first with `installer/update-manifest.ps1 -Verify`. Later,
   upload `stable/win-x64.json` from the same run to promote it.

## Consequences

- Nothing changes on any machine until the trust file is filled in and a signed build ships it.
- A technician without administrator credentials cannot apply an update; the card says so before they
  press Install, and the MSP's own push keeps working as before.
- The IPC contract moves to version 5 (`get_update`, `install_update`, `set_update_channel`, `update`).
- The UAC prompt's wording, and that Windows Installer raises it under `/passive` when started without
  elevation, are checked on the laptop with the first signed build (`docs/dev/installer.md`).

## Alternatives considered

- **Comparing the leaf thumbprint.** Rejected: every Artifact Signing release has a new leaf (ADR-0007).
- **Signing the manifest with the Authenticode certificate.** Rejected: Artifact Signing signs files, not
  arbitrary bytes, and its certificates rotate; a separate long-lived key is simpler to pin.
- **A private key in a GitHub secret.** Rejected: a key that can be read by the job can be copied by it.
- **Downloading during a session.** Rejected: it shares a customer's support connection.
