# ADR-0007: The Windows installer

- **Status:** **Proposed 2026-10-27** (ST-112). Built and smoke-tested in CI; the owner decides, and
  confirms the uninstall's treatment of technicians who are not signed in (below, "Upgrade and
  uninstall") before this leaves Proposed. Amends ADR-0003 in one place: an installed service starts
  from the machine's Run key, not the user's. Revised 2026-10-28 after review: the install folder is
  fixed, signing credentials reach only the signing steps, and the uninstall removes the key and
  credentials of technicians who are not signed in.
- **Date:** 2026-10-27
- **Ticket:** ST-112 (ST-113, auto-update, builds on it)
- **Deciders:** project owner; build agent

## Context

ST-112 asks for a signed installer that MSPs can deploy silently, that starts the capture service at
sign-in, and whose uninstall leaves nothing, in under 250 MB without the speech models. Four facts
shape it:

1. **The service is a per-user background process, not a Windows Service** (ADR-0003). Hooks and
   capture only work inside the signed-in user's session, so it starts from a Run key at sign-in. The
   UI runs on demand, or at sign-in when the technician turns "Start UI at login" on.
2. **MSPs deploy with Intune and RMM agents**, which install as the system account, silently, and
   detect success by exit code and product code. A per-user install run that way lands in the system
   account's profile, where nobody ever runs it.
3. **The peer checks trust a file, not a process** (ADR-0003, ST-012). In a signed build each process
   accepts the other only when the same publisher signed it; in an unsigned build, only when it sits in
   the same folder. Two gaps were left to packaging: the startup-hook switch lived in a runtime file
   beside the executable, and the managed code beside a signed launcher was not signed.
4. **Whisper's native libraries import the Visual C++ 2015-2022 runtime** (MSVCP140, VCRUNTIME140,
   VCRUNTIME140_1, VCOMP140), which a fresh Windows does not have. Found while building this; the CI
   build lists it on every run.

## Options compared

| | **MSI, per machine (WiX 5)** | MSIX | EXE only (Inno Setup, NSIS) | MSI, per user |
|---|---|---|---|---|
| Intune | Line-of-business app, product code detection built in | Supported | Win32 app, detection rule written by hand | Only in user context |
| RMM agents, GPO | `msiexec /qn` everywhere; GPO assigns MSIs | Few RMM tools; no GPO | Each tool's own switches | Runs as SYSTEM into the wrong profile |
| Silent switches, exit codes, logging | Standard (`/qn`, `/l*v`, 0/3010/1603) | `Add-AppxPackage`, no MSI codes | Per tool | Standard |
| Start at sign-in for every user | Machine Run key | Startup task, enabled only after the user starts the app once | Machine Run key | User Run key, installing user only |
| Process model (two exes, named pipe, mutex) | Unchanged | Runs in a package identity; the inner executables are not signed by our certificate, only the package is, so the peer checks would read every build as unsigned | Unchanged | Unchanged |
| Who can change the installed files | Administrators only (Program Files) | Nobody (WindowsApps) | Administrators only | The user (`%LOCALAPPDATA%\Programs`), so the files the peer checks vouch for are the user's to swap |
| Unsigned CI smoke test | Installs | Will not install unsigned | Installs | Installs |
| Upgrade path for ST-113 | Major upgrade by UpgradeCode | Built in | Per tool | Major upgrade, no elevation |

**Chosen: a per-machine MSI built with WiX 5, plus a WiX setup program for the runtime.** It is the
format every MSP tool already speaks, it installs into the folder only administrators can change, and
it leaves the process model of ADR-0003 exactly as it is. MSIX fails on the peer checks and on
start-at-sign-in for a user who has never opened the app. A per-user MSI fails on RMM deployment and
puts the trusted files in a folder the user can write.

WiX 5.0.2 rather than 6 or 7: from version 6 the WiX binaries carry an Open Source Maintenance Fee for
users with revenue over US$10,000 a year. Version 5 is MS-RL with no fee, and nothing here needs a
later version. Moving up is a decision for the owner once there is revenue.

## Decision

### What is built (`installer/`, `client/Packaging.props`)

- **`ScreenTail.msi`**: per machine, x64, into `C:\Program Files\ScreenTail`, both executables side by
  side with the native libraries and the registry files the service reads. A fixed UpgradeCode, so a
  newer version replaces the old one; a downgrade is refused. **The folder cannot be changed**: the
  package works out the machine's own Program Files from the environment and refuses any
  `INSTALLFOLDER` that differs, because the uninstall runs the installed service as the system
  account and that file must sit where only administrators can change it.
- **`ScreenTail-Setup.exe`**: Microsoft's Visual C++ runtime installer (downloaded at build time and
  checked to be Microsoft-signed), skipped when the same or a newer runtime is there and never removed
  on uninstall because other software shares it, then the MSI. The MSI on its own refuses to install
  without the runtime, with a message naming it, rather than install a service whose speech fails; an
  MSP that already deploys the runtime can use the MSI alone.
- **`installer/build.ps1`** builds both, the same way on a developer's Windows machine and on CI:
  whole, or one stage at a time (payload, package, setup program, reattach), and a stage never signs.

### How the processes are published

- **Self-contained and single-file, for the installer build only** (`-p:ScreenTailInstaller=true`).
  The runtime configuration, including `StartupHookSupport=false`, and every managed assembly sit inside
  each executable, so the executable's signature covers them. Both gaps ADR-0003 left to packaging for
  managed code are closed. Development builds, tests and the demo are unchanged.
- **Native libraries stay beside the executables** (`IncludeNativeLibrariesForSelfExtract=false`). A
  single-file build that carries them unpacks them at run time into a folder under the user's profile,
  which the user can write to. In Program Files they sit where only administrators can change them, and
  the signing step signs each one that is not already signed. This is the closest safe form: .NET
  cannot load a native library from inside the bundle.
- **Diagnostics cannot be turned off from inside the build.** Checked on .NET 10.0.12 on 2026-10-27: a
  test program with `EnableDiagnostics=0` in its runtime configuration still opened the diagnostics
  channel; `DOTNET_EnableDiagnostics=0` in the environment closed it. The runtime reads that switch and
  the profiler variables from the environment only, before any managed code runs. `RunningHonestly`
  stays the check at the top of `Main`. Closing this fully would need Native AOT, which WPF does not
  support.
- **The installed service is a Windows program, not a console program**, so starting at sign-in opens
  no console window. Its `--capabilities` and `--transcribe` modes still write to redirected output.
- **x64 only.** Whisper.net ships ARM64 natives; an ARM64 package is a follow-up if technicians need it.

### Start at sign-in (amends ADR-0003)

The MSI writes `HKLM\Software\Microsoft\Windows\CurrentVersion\Run\ScreenTail.Service`, the quoted
path of the installed service, so it starts for whoever signs in. Same value name as the development
helper `LoginStartup`, and the single-instance mutex means a developer's own entry cannot start a
second copy. The UI is not started by the installer: it has a Start menu shortcut, and "Start UI at
login" in Settings writes the technician's own Run value as before. Nothing is restarted after an
install or upgrade; the service starts at the next sign-in, which is what the ticket asks.

### Upgrade and uninstall

- **Running copies are ended** before files are replaced or removed, on an upgrade or an uninstall and
  on nothing else (a first install has nothing to close, and a repair must not end a recording), so a
  silent push while somebody is signed in needs no restart. That reaches every session when the
  installer runs as the system account, as MSP tools run it. **After an upgrade the service is down
  until the technician's next sign-in**; a session that was recording is recovered then (ST-020).
- **Windows Installer removes what it installed**: the files, the machine Run value, the shortcut.
- **On uninstall, and never during an upgrade**, the installed service runs once in an uninstall mode,
  as the system account. It stops any running copy, removes the Run values the UI wrote in signed-in
  users' registries, and then, for each profile's data folder (`%LOCALAPPDATA%\ScreenTail`):
  - **A technician who is signed in:** the whole folder is deleted **with that technician's own token**,
    the store's key and the device's credentials first (Delete everything's order), so the delete has
    no more rights than the folder's owner. If that fails, the next rule applies.
  - **A technician who is not signed in** (an RMM uninstall at night): the system account never deletes
    a folder in somebody else's profile. It removes **only the store's key, the device's refresh token
    and its temporary file, and the pipe token**, one file at a time: the profile folder is opened
    without following a link, each folder below it and then each file is opened by name relative to the
    folder above with links refused, checked through its handle (not a link, the expected kind, the
    same volume, a file with one name) and deleted through that handle. **The encrypted store stays and
    cannot be read without its key** (crypto-shredding). Also left: files with no session content
    (settings, the cached tenant policy, the UI's preferences, the speech models, stack-only crash
    reports), and the folder itself. Anything in doubt is left and counted.
  - **A link anywhere between the profile and the data folder, or a profile path that cannot be read
    plainly:** nothing there is touched.
  The step writes how many of each into the uninstall log, as counts only.
- **INV-12 after an uninstall**: a signed-in technician's data is gone; a signed-out technician's store
  is unreadable and their device credential is gone, with the empty shell of the folder left for them or
  an administrator to delete. The retention purge does not run once the service is uninstalled, which is
  why the key goes rather than waiting for it. **This is the practical default the owner asked for and
  is a decision to confirm**: the alternative is a component that runs at each technician's next
  sign-in to delete the rest (see Alternatives).
- A technician whose Local AppData has been redirected somewhere other than `AppData\Local` in their
  profile (Windows does not support that, though policies exist) is not found, and keeps their folder.

### Signing

- **One step, `installer/sign.ps1`**, run on every executable and library in the payload that is not
  already signed (Microsoft's WPF libraries are), on the MSI, and on the setup program and its engine.
  SHA-256 with an RFC 3161 timestamp, so a signature stays valid after its certificate expires, which is
  what `Authenticode.cs` expects. Afterwards every file must verify, carry a timestamp, and share one
  signer, because the peer checks compare signers.
- **Recommended: Azure Artifact Signing** (formerly Trusted Signing): no key to hold, and the
  certificate chains to a root Windows trusts. CI signs through **workload identity federation**: the
  Azure app registration trusts this repository's `signing` environment, the signed job alone has
  `id-token: write`, and `sign.ps1` exchanges the job's GitHub OIDC token. No client secret exists. A certificate on a hardware
  token or a cloud HSM works too, through the Windows certificate store. No key file is supported: a
  private key in a file is the thing a public repository must never hold.
- **Unconfigured, it signs nothing and says so**, listing what it would have signed. An unsigned build
  is a development build, and the peer checks treat it as one; nothing about them changed.
- In CI the unsigned job runs everywhere; the signed job runs only on main, only when the repository
  variable `SCREENTAIL_SIGNING` is `true`, in the `signing` environment. **The credential reaches only
  the signing steps**: the build runs in stages with no signing setting in their environment, each
  stage drops the OIDC request variables before running any build code (NuGet packages' build targets,
  WiX, downloads), and a `sign.ps1` step between stages is the only place the token is asked for. A
  check in the workflows job holds all of this.
- **Artifact Signing issues short-lived certificates that rotate.** Both executables are signed in one
  `signtool` call, and `sign.ps1` fails unless every file in a call has the same signer, so the peer
  checks' leaf-thumbprint comparison holds for an installed pair. Anything that compares builds made
  at different times must compare the publisher's identity instead (next section).

### The seam for ST-113 (auto-update)

- The UpgradeCode is fixed and every package is a major upgrade, so applying an update is
  `msiexec /i ScreenTail.msi /qn`, and user data survives it (CI checks that).
- One version flows into both executables and the package, so the running service knows what it is.
- **Per machine means an update needs administrator rights**, which neither process has. ST-113 needs
  one of: the MSP's own tool pushing updates (the usual MSP answer); a scheduled task the installer
  registers that runs an elevated updater as the system account; or a small Windows service that only
  updates. It also needs the release feed (ST-007's bucket), a check that a downloaded package is
  signed by the same publisher as the running service, and "never during a session", which the service
  knows. **That check must compare publisher identity (the certificate's subject, or its chain up to
  the Artifact Signing identity), not the leaf thumbprint**: Artifact Signing rotates its certificates
  every few days, so next week's release carries a different leaf from today's install.
  `Authenticode.VerifiedSignerThumbprint` answers the narrower question and is not enough on its own.

## Consequences

- The ticket's acceptance criteria split three ways. Silent install, start at next sign-in, clean
  uninstall within this ADR's definition, and size are checked by CI's smoke test on every client
  change. "No SmartScreen or antivirus warning on a fresh Windows 11" needs the certificate, and with
  Artifact Signing, reputation that builds over the first downloads; it can only be checked then.
- A technician who is not signed in when an MSP uninstalls keeps an unreadable store and the files
  listed above in their profile, until they or an administrator delete the folder; a value they wrote
  for "Start UI at login" stays in their registry, naming a program that no longer exists, which
  Windows passes over. "Delete everything" in Settings remains the full erase while ScreenTail is
  installed.
- The backend address is still read from `SCREENTAIL_BACKEND` only. How an installed client learns it
  (a built-in default, an MSI property, the invite code) is not decided here.

## Alternatives considered

- **A Windows Service for the update and cleanup work.** Would let the uninstall finish every user's
  data at their next sign-in and give ST-113 its elevated updater, at the cost of a privileged process
  running all day. Left for ST-113 to decide.
- **Deleting signed-out technicians' whole folders as the system account.** Rejected: the system
  account does not delete folders inside a profile its owner controls. The handle-bound removal of four
  named files is the most it does there.
- **Active Setup for per-user cleanup.** Runs a command as each user at their next sign-in, but leaves a
  machine key behind for ever and would run against a later reinstall's data unless carefully versioned.
- **Shipping the Visual C++ runtime files beside Whisper.** Removes the setup program, but depends on
  the build machine's Visual Studio licence to redistribute them and leaves them out of Windows Update.
