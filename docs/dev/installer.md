# The Windows installer

**Written 2026-10-27, revised 2026-10-28, ST-112.** Why it is built this way is ADR-0007. This page is how to build it, how
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
sizes against the ticket's 250 MB limit. It downloads WiX 5 (a local .NET tool), two WiX extensions and
Microsoft's runtime installer, and checks the signature of each.

`-Stage Payload`, `Package`, `Bundle` and `Reattach` run one part at a time and never sign; CI uses them
so that only its `sign.ps1` steps could ever reach a signing credential (ADR-0007). On a developer's
machine the whole build, without `-Stage`, is the one to use.

CI builds it on every pull request that touches `client/` or `installer/` (the **Installer (Windows)**
job): it installs, upgrades over an older build, runs both installed programs, uninstalls, and checks
what each step left. The unsigned packages are a workflow artifact, `screentail-installer-unsigned`,
kept seven days. To try one: download it from the run's summary page, on a throwaway VM.

`installer\smoke-test.ps1` is that check, and can be run on a VM by hand. It uninstalls ScreenTail and
deletes its data folder, so it refuses to run where ScreenTail is installed unless given `-Force`.

## Switch signing on

Nothing in the repository changes when the certificate arrives: signing is configured where the
repository cannot see it, and `installer\sign.ps1` is already the step every file goes through.

**Recommended: Azure Artifact Signing** (formerly Trusted Signing). No key to keep, CI can sign, and
Windows trusts the certificate's root.

1. Create an Artifact Signing account and a certificate profile in Azure (identity validation takes
   a few days). Note the account's endpoint, its name and the profile's name.
2. Create an app registration and give it the certificate profile signer role on the profile
   (*Trusted Signing Certificate Profile Signer* before the service was renamed). **Do not create a
   client secret.** Instead add a *federated credential* to it: scenario *GitHub Actions deploying Azure
   resources*, organisation `hiratinspace`, repository `ScreenTail`, entity type *Environment*, name
   `signing`. Azure then accepts this repository's signing job, and nothing else, with no secret.
3. In GitHub, *Settings → Environments → New environment* `signing`. Under *Deployment branches*, allow
   `main` only. Add the environment **variables** (none of these is secret) `AZURE_TENANT_ID`,
   `AZURE_CLIENT_ID` (the app registration's), `SCREENTAIL_SIGNING_ENDPOINT`,
   `SCREENTAIL_SIGNING_ACCOUNT` and `SCREENTAIL_SIGNING_PROFILE`.
4. *Settings → Secrets and variables → Actions → Variables*: add the repository variable
   `SCREENTAIL_SIGNING` = `true`. This is the switch. Create the `signing` environment (step 3) first:
   the signed job names that environment only while the switch is on, and GitHub holds a run that names
   an environment which does not exist.

The signed job is the only one with permission to ask GitHub for an OIDC token. It builds in stages
with no signing setting in reach, and between stages a `sign.ps1` step asks for the token, hands it to
`signtool` in a temporary file and deletes it. No job in the repository names a secret.

What actually keeps the signing credential safe is the signed job's own conditions (main only, the
`signing` environment restricted to main) and Azure trusting that environment alone. The check in the
`workflows` job that holds the shape of these jobs is a second layer, meant to catch an edit that would
weaken those or widen what can reach the token before it merges, not the protection itself. It tries
itself first on made-up workflows that must pass and must fail, so a check that waved everything through
would fail rather than be trusted.

From then on every merge that touches the client also runs **Installer (Windows, signed)**: the same
build with every executable, library and installer signed and timestamped, the same smoke test (now with
the peer checks taking the signed path), and the artifact `screentail-installer`. It never runs for a
pull request, and it fails rather than uploading an unsigned build if the configuration is incomplete.

**On a developer's machine**, set the same three `SCREENTAIL_SIGNING_*` variables in the shell and sign in
with `az login`, or, for a certificate on a hardware token or a cloud HSM that appears in the Windows
certificate store, set `SCREENTAIL_SIGNING_THUMBPRINT` (and optionally `SCREENTAIL_TIMESTAMP_URL`). Then
run `build.ps1` as above. A private key in a file is deliberately not supported. Nothing about signing is
ever committed.

When signing first works, record it: run the signed package on a fresh Windows 11 with Defender on, and
write down what SmartScreen says (ticket AC1). Artifact Signing builds reputation over the first downloads.

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
  run by hand from one user's session, it may only reach that session. **After an upgrade the service
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
