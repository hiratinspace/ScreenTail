# The Windows installer

**Written 2026-10-27, revised 2026-10-29, ST-112 and ST-113.** Why it is built this way is ADR-0007, and ADR-0008 for updates. This page is how to build it, how
signing is switched on, how an MSP deploys it, and what an uninstall removes.

Two files come out of a build:

| File | For |
|---|---|
| `ScreenTail.msi` | MSP deployment (Intune, RMM, GPO). Per machine. Needs the Visual C++ 2015-2022 x64 runtime already on the machine, and refuses to install without it |
| `ScreenTail-Setup.exe` | Any machine, including a fresh Windows: installs that runtime if it is missing or older, then the MSI |

Both install to `C:\Program Files\ScreenTail` for every user of the machine, start the capture service
at each sign-in, and put ScreenTail in the Start menu. **Until the code-signing certificate exists every
build is unsigned**: a development build, which Windows SmartScreen will warn about and which the two
processes accept from each other only because they sit in the same folder. **Never deploy an unsigned
build to a technician's machine.** It is for test VMs: the peer checks run in their development form,
and nothing tells a user that the program is ours.

The install folder is always `C:\Program Files\ScreenTail` (the machine's own 64-bit Program Files).
Setting `INSTALLFOLDER` on the command line makes the install fail rather than put ScreenTail somewhere
ordinary users can change, because the uninstall runs the installed service as the system account.

## Build it

On a Windows machine with the .NET 10 SDK, from a clone:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build.ps1 -Version 0.1.0
```

The packages land in `artifacts\installer\packages`. The script prints each step: the payload it
published and checked, what the native libraries need, what it signed (or would have signed), and the
sizes against the ticket's 250 MB limit. It downloads WiX 5 and its two extensions from nuget.org, and
refuses each unless its SHA-256 is the one pinned in `installer\tools.json` and its NuGet signatures
verify. It also downloads Microsoft's runtime installer and checks that Microsoft signed it.

`-Stage Payload` publishes and checks the payload alone, and never signs. `installer\package.ps1`
wraps a payload one step at a time (`-Step Msi`, `Bundle`, `Detach`, `Reattach`) and signs nothing;
`build.ps1` calls it with a `sign.ps1` step between each, because every layer is signed before the next
one wraps it (ADR-0007). On a developer's machine the whole build, without `-Stage`, is the one to use.
To move to a newer WiX or signing client, change its version and hash in `tools.json` in a reviewed
pull request.

CI builds it on every pull request that touches `client/` or `installer/` (the **Installer (Windows)**
job): it runs the same steps the signing job runs, with nothing signed, then installs, upgrades over an
older build, runs both installed programs, uninstalls, and checks what each step left. The unsigned packages are a workflow artifact, `screentail-installer-unsigned`,
kept seven days. To try one: download it from the run's summary page, on a throwaway VM.

`installer\smoke-test.ps1` is that check, and can be run on a VM by hand. It uninstalls ScreenTail and
deletes its data folder, so it refuses to run where ScreenTail is installed unless given `-Force`.

## Switch signing on

Nothing in the repository changes when the certificate arrives: signing is configured where the
repository cannot see it, and `installer\sign.ps1` is already the step every file goes through.

**Recommended: Azure Artifact Signing** (formerly Trusted Signing). No key to keep, CI can sign, and
Windows trusts the certificate's root.

### Owner steps (done by the repository owner in Azure and in GitHub's settings, not by a pull request)

None of these is in the repository, and nothing in CI creates or changes them.

1. **Azure:** create an Artifact Signing account and a certificate profile (identity validation takes
   a few days). Note the account's endpoint, its name and the profile's name.
2. **Azure:** create an app registration and give it the certificate profile signer role on the
   profile (*Trusted Signing Certificate Profile Signer* before the service was renamed). **Do not
   create a client secret.** Add a *federated credential* to it instead: scenario *GitHub Actions
   deploying Azure resources*, organisation `hiratinspace`, repository `ScreenTail`, entity type
   *Environment*, name `signing`. Its subject is then exactly
   `repo:hiratinspace/ScreenTail:environment:signing`, so Azure accepts a token from the signing job
   in that environment and from nothing else.
3. **GitHub, *Settings → Environments → New environment*:** `signing`.
   - **Mandatory: under *Deployment branches and tags*, choose *Selected branches and tags* and allow
     `main` only.** Azure trusts the environment, not the branch. Without this rule, a workflow run
     from any other branch that names the `signing` environment gets a token Azure accepts, and can
     sign whatever that branch builds. The job's own "main only" condition lives in a file a branch
     can change; this rule does not.
   - **Recommended by default: *Required reviewers*** (the owner), so every signing run waits for a
     person to approve it.
   - Add these environment **variables** (none of them is a credential): `AZURE_TENANT_ID`,
     `AZURE_CLIENT_ID` (the app registration's), `SCREENTAIL_SIGNING_ENDPOINT`,
     `SCREENTAIL_SIGNING_ACCOUNT` and `SCREENTAIL_SIGNING_PROFILE`.
4. **GitHub, *Settings → Secrets and variables → Actions → Variables*:** add the repository variable
   `SCREENTAIL_SIGNING` = `true`. This is the switch, and the last step: before it, no signing job runs.

### What CI then does

Every merge to main that touches the client or the installer runs three installer jobs:

- **Installer (Windows)**, as on a pull request, with no token and no signing setting. It also hands
  the checked payload, and only that, to the next job as the artifact `screentail-installer-payload`
  (kept one day), with the artifact's id, its digest and the SHA-256 of every file as job outputs.
- **Installer (Windows, signing)**, the only job that may ask GitHub for an OIDC token, always in the
  `signing` environment. It checks out the same commit, downloads the payload by the id the first job
  reported, and refuses to sign unless the artifact's digest and every file match what the first job
  recorded. Then it runs one exact command per step: sign the payload, build the MSI from it, sign the MSI, build the setup program from that, detach its engine,
  sign the engine, reattach it, sign the setup program, check every signature, upload the artifact
  `screentail-installer`. It runs no restore, no build and nothing compiled from the repository, and
  nothing it runs comes from the first job: WiX is downloaded and checked against `tools.json` in this
  job. Each `sign.ps1` step asks for the token itself, hands it to `signtool` in a temporary file and
  deletes it. It fails rather than upload an unsigned build if the configuration is incomplete.
- **Installer (Windows, signed, smoke test)**, with no token: the smoke test on the signed build, now
  with the peer checks taking the signed path.

**Installer (Windows)** runs on every pull request that touches the client or the installer, as
above. Only the payload handoff, the signing job and the signed smoke test never run for a pull
request.

What actually keeps the signing token safe is the signing job's own conditions (a push to main, the
switch), the `signing` environment's branch rule, and Azure trusting that environment alone. The check
in the `workflows` job (`scripts/ci/check_installer_jobs.py`) is a second layer: it holds the signing
job's exact steps, how they run, its permissions, the pinned actions and tools, and the WiX authoring,
so an edit that would weaken them fails before it merges. It tries itself first on made-up samples
that must pass and must fail, so a check that waved everything through would fail rather than be
trusted.

**On a developer's machine**, set the same three `SCREENTAIL_SIGNING_*` variables in the shell and sign in
with `az login`, or, for a certificate on a hardware token or a cloud HSM that appears in the Windows
certificate store, set `SCREENTAIL_SIGNING_THUMBPRINT` (and optionally `SCREENTAIL_TIMESTAMP_URL`). Then
run `build.ps1` as above. A private key in a file is deliberately not supported. Nothing about signing is
ever committed.

When signing first works, record it: run the signed package on a fresh Windows 11 with Defender on, and
write down what SmartScreen says (ticket AC1). Artifact Signing builds reputation over the first downloads.

## Updates (ST-113)

Why it works this way is ADR-0008. In short: the capture service checks a signed manifest on a static
HTTPS feed, downloads a newer release in the background while no session is recording, and Settings →
Updates offers **Install update** when it is ready. Pressing it checks the package again and starts
Windows Installer, which asks for an administrator's approval (UAC) for the per-machine package.
ScreenTail closes while it installs, and the capture service starts again at the next sign-in. MSPs
that push updates with their own tools keep doing so; an admin can turn checks off (`updateCheckHours` 0).

**Nothing is switched on in the repository yet.** `shared/release/update-trust.json`, compiled into the
client, has no feed and no key, so a build makes no update request at all and Settings says so. A build
nobody signed never updates itself either.

### What the client checks

- The manifest's ECDSA P-256 signature, with a key compiled into the client, before anything in it is read.
- Its channel (the device's: stable or pilot; the channel is inside the signed bytes), its architecture,
  a version newer than the installed one and not older than one this installation already saw, an
  installed version not older than the release's minimum, a package URL on a compiled-in host, a
  well-formed SHA-256 and a size up to 512 MB. Any failure is refused and logged (`Update refused:` and
  a fixed sentence; never the manifest's text).
- The download's size and SHA-256. Then, just before Windows Installer starts, the same again through
  a handle that stops the file being changed, renamed or deleted, plus its Authenticode signature, its
  timestamp and its publisher, compared by identity (subject and Artifact Signing's validation EKU), not
  by leaf certificate.
- Never during a session: Install is refused while recording, and no session starts while Windows
  Installer runs.

### Admin policy

`--set-policy <tenant-id> update-channel=stable|pilot update-channel-locked=true|false update-hours=0-168
update-local-only=true|false`. Defaults: stable, chosen by the technician, every 24 hours, and **no update
checks under local-only**. `update-local-only=true` lets update checks through a local-only the admin
locked on (`local-only=true locked=true`) and nothing else: a local-only the technician turned on
themselves stops update checks whatever this says.

### Switch it on (owner steps)

None of these is in the repository except the trust file, which changes in a reviewed pull request.

1. **Azure Key Vault:** a vault (Premium, for an HSM-backed key) and an **EC P-256** key for signing. Give
   the signing app registration a role on that key that allows only `keys/sign/action` (a custom role;
   *Key Vault Crypto User* works but allows more). Note the key identifier **with its version**.
2. **The public key, in a pull request:** `az keyvault key download --vault-name <vault> --name <key>
   --encoding DER --file key.der`, then its base64 into `shared/release/update-trust.json` under `keys`
   with an id. Never commit the key file itself to anything but that one public value.
3. **Hosting (ST-007), in the same pull request:** the feed URL (an HTTPS folder ending in `/`) and the
   exact host names packages are served from, into `feed` and `packageHosts`. **If a CDN redirects the
   download, list every host it redirects to, exactly, in `packageHosts` too**: the client follows a
   redirect only onto a listed host, so an unlisted CDN edge host makes every download fail.
4. **GitHub, the `signing` environment:** variables `SCREENTAIL_UPDATE_KEY` (the versioned key
   identifier) and `SCREENTAIL_UPDATE_BASE` (the packages' HTTPS folder, ending in `/`). From then on
   every signed build on main also writes `pilot/win-x64.json` (artifact `screentail-update-manifests`,
   kept 90 days). **Builds are signed for pilot only.**
5. **GitHub, a new environment `release-stable`**, for promotion to stable. Mandatory: deployment
   branches `main` only, and required reviewers (the owner). Variables: `SCREENTAIL_STABLE_RELEASE` =
   `true`, `SCREENTAIL_UPDATE_KEY`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`. In Azure, a second federated
   credential on the app registration for entity type *Environment*, name `release-stable`. Until this
   exists, the promotion workflow fails closed. **Never set `SCREENTAIL_STABLE_RELEASE` as a repository
   or organisation variable**: a job falls back to those when its environment has no such variable, which
   would open the gate.

### Release to pilot, then promote to stable

1. From a signing run on main, upload `ScreenTail.msi` to `<base><version>/ScreenTail.msi` and
   `pilot/win-x64.json` to `<feed>pilot/win-x64.json`. Check it first:
   `powershell -File .\installer\update-manifest.ps1 -Verify pilot\win-x64.json`.
2. To promote that release: *Actions → promote-stable → Run workflow* on main, with the ci run's id and
   the version, and approve it. It first checks that the run is a successful ci.yml run of a push to main,
   and refuses anything else. Upload the `stable/win-x64.json` it produces (artifact
   `screentail-stable-manifest`, kept one day) to `<feed>stable/win-x64.json`.

### Laptop checks, with the first signed build

The hosted runner has no desktop, so these are checked once by hand on the laptop and written down:

- Install update from Settings under a standard account: the UAC prompt appears, names the package and
  its verified publisher, and asks for administrator credentials; cancelling leaves the update ready.
- The same under an administrator account: the consent prompt, then the upgrade, with user data kept.
- During a recording the button is disabled, and the tray says nothing about the update until it ends.

## Deploy it silently (for MSPs)

Every command runs as an administrator or the system account, which is what Intune and RMM agents use.

```powershell
# Machines that may not have the Visual C++ runtime (any fresh Windows)
ScreenTail-Setup.exe /quiet /norestart /log C:\Windows\Temp\screentail-setup.log

# Machines where the runtime is deployed separately
msiexec /i ScreenTail.msi /qn /norestart /l*v C:\Windows\Temp\screentail-install.log
```

- **Intune:** add `ScreenTail.msi` as a *line-of-business app* (product code detection is automatic), with
  the Visual C++ 2015-2022 x64 runtime as a Win32 app it depends on; or wrap `ScreenTail-Setup.exe` as a
  Win32 app with the commands above and detect the file `C:\Program Files\ScreenTail\ScreenTail.Service.exe`.
- **RMM tools:** run either command above. Exit code `0` is success, `3010` success with a restart
  pending, `1618` another installation in progress (retry), anything else a failure; the log says why.
- **Upgrades:** deploy the newer package the same way. It replaces the old version and keeps every
  user's data. Running ScreenTail processes are ended first, so no restart is needed. That reaches
  every signed-in user when the installer runs as the system account (as Intune and RMM tools run it);
  run by hand from one user's session, it may only reach that session. Where Windows Installer's Restart
  Manager reaches a session (the session the installer was started from), it asks first, and both
  programs close by themselves: the service stops capture and closes its store, and a session that was
  recording is left for the next start to recover, exactly as after a crash. **After an upgrade the service
  stays down until each technician's next sign-in**, so schedule upgrades outside working hours; a
  session that was recording is recovered when the service next starts. A first install or a repair
  ends nothing.
- The service starts at each user's **next sign-in**. Nothing starts for a user who is already signed in
  until then. The UI opens from the Start menu (or at sign-in, if the technician turns "Start UI at login"
  on in Settings).

## Uninstall it

```powershell
# Installed with the setup program
"C:\ProgramData\Package Cache\<id>\ScreenTail-Setup.exe" /uninstall /quiet /norestart
# or, from the same file that installed it
ScreenTail-Setup.exe /uninstall /quiet /norestart

# Installed with the MSI
msiexec /x ScreenTail.msi /qn /norestart /l*v C:\Windows\Temp\screentail-uninstall.log
# or by product code, from Programs and Features or:
#   Get-Package ScreenTail | Select-Object -ExpandProperty FastPackageReference
```

Or *Settings → Apps → ScreenTail → Uninstall*.

**What an uninstall removes, for everybody:** the program files, the Start menu shortcut, the machine
entry that starts the service at sign-in, every running ScreenTail process, and the "Start UI at login"
entry of every signed-in user.

**Usually no restart needed.** Before Windows Installer deletes the program files, the uninstall waits
until no other program has one of them open, and checks twice, a quarter of a second apart. Virus
scanners and endpoint agents read a program's file while it runs, and one still reading
`ScreenTail.Service.exe` when the files go would make Windows Installer leave it for a restart (exit
code `3010`, the folder left behind until then). The log says how long it waited and how many files were
still open (`waited ... ms for other programs`). A restart can still be asked for when:

- a program keeps a file open for longer than the wait (15 seconds at most, less when stopping running
  copies and removing data took a while: the whole step has to end inside the minute Windows
  Installer's helper gives it); the log then says how many files were still open;
- a program opens a file in the moment after the second check, before the step exits;
- a running ScreenTail copy could not be stopped.

**Running copies close by themselves when Windows asks.** The capture service has one hidden window
for this (ST-112): at sign-out, at shutdown, when Restart Manager asks for an installer, and on a
`WM_CLOSE` (`taskkill /im ScreenTail.Service.exe` without `/f`), it stops capture, releases the
microphone, closes its store and exits, in well under a second when idle and never holding Windows more
than four seconds. A session that was recording is not finalized then: it is recovered as partial at the
next start, as after a crash, because finishing a note needs up to twenty seconds of redaction that
Windows does not give. The UI already closed on Windows' request. In the uninstall log, the step's line
`N running copies stopped` counts only the copies Restart Manager did not close: other users' sessions
when the installer runs as the system account, and any session where another program (a scanner, an
endpoint agent) held an installed file at that moment, because ScreenTail's package asks only programs
registered for restart and asks nobody when something else is in the way (`MSIRMSHUTDOWN=2`).

**Other programs' installers can close ScreenTail, and it starts again.** Any installer that replaces a
file ScreenTail has loaded, a silent Visual C++ runtime update for one, can now ask it to close through
Restart Manager, and it does. The service is registered for restart, so Restart Manager starts it again
when that installer is done, in its ordinary state: watching, not recording. A session that was recording
then is split: the part before the close is recovered as partial when the service starts again, and the
technician starts a new one if the support session goes on. ScreenTail's own installer does not
restart it (`MSIDISABLERMRESTART`): after a ScreenTail upgrade or uninstall the service stays down until
the next sign-in, as above. The UI is registered for restart too, so a running UI does not keep
Windows Installer from asking the service. Another program holding an installed file (a virus scanner,
an endpoint agent) still does: ScreenTail's package then asks nobody, and the service is ended as
before. **Neither process runs elevated**: started with "Run as administrator", restarted by an
elevated installer, run as a user who is not the one signed in, or in session 0, the service or the UI
exits with 0 and starts normally at the next sign-in (or, for the UI, from the Start menu). Each refusal
leaves a warning in the Windows Application log, source `ScreenTail`, event 1100 to 1103 for the service
and 1200 to 1203 for the UI, with a fixed sentence and nothing about the user. Run the service from a
normal, not elevated, prompt on a developer's machine too, unless UAC is off. Not yet measured: how long the
service takes to stop in the middle of a recording, with speech, OCR, the microphone and the hooks all
busy. The idle figure is about 150 ms on CI, and the four-second limit holds whatever happens; the
laptop is where the busy figure has to be taken.

`3010` still means the uninstall succeeded; the files left are deleted at the next restart.

**For a technician who is signed in at that moment:** their whole ScreenTail data folder,
`%LOCALAPPDATA%\ScreenTail`: the recorded sessions and drafts, the store's key, the device's sign-in
token, the speech models, settings and crash reports. The key and the token go first. It runs with that
technician's own rights.

**For a technician who is not signed in** (an RMM uninstall at night, a colleague's account on a shared
machine): the store's key, the device's sign-in token and its temporary file, and the pipe token are
removed, and nothing else. **The recorded sessions and drafts stay on disk but can no longer be read**:
they are encrypted, and the key is gone. Also left in their folder: files with no session content
(settings, the cached tenant policy, the UI's preferences, the speech models, stack-only crash reports),
and the folder itself. They, or an administrator, delete `%LOCALAPPDATA%\ScreenTail` in that profile to
clear the rest.

**Left alone, everywhere:**

- a data folder reached through a link (a junction or a symbolic link), and any file in a data folder
  that is a link or otherwise not what it should be;
- the data folder of a technician whose Local AppData is redirected away from their profile;
- a "Start UI at login" entry in the registry of a technician who is not signed in. It names a program
  that no longer exists, and Windows passes over it;
- the Visual C++ runtime, which other software shares.

The uninstall log counts each of these: search it for `ScreenTail uninstall:`.

**To erase a technician's data completely, use "Delete everything" in ScreenTail's Settings while
ScreenTail is still installed** (INV-12). It removes the store, its key and the device's token, and asks
first. Once ScreenTail is uninstalled that button is gone with it, and the rules above are what apply.
