# ADR-0007: The Windows installer

- **Status:** **Proposed 2026-10-27** (ST-112). Built and smoke-tested in CI; the owner decides. Amends
  ADR-0003 in one place: an installed service starts from the machine's Run key, not the user's.
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
  newer version replaces the old one; a downgrade is refused.
- **`ScreenTail-Setup.exe`**: Microsoft's Visual C++ runtime installer (downloaded at build time and
  checked to be Microsoft-signed), skipped when the same or a newer runtime is there and never removed
  on uninstall because other software shares it, then the MSI. The MSI on its own refuses to install
  without the runtime, with a message naming it, rather than install a service whose speech fails; an
  MSP that already deploys the runtime can use the MSI alone.
- **`installer/build.ps1`** builds both, the same way on a developer's Windows machine and on CI.

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

- **Running copies are ended** in every session before files are replaced or removed, on upgrade and
  uninstall alike, so a silent push while somebody is signed in needs no restart. A session that was
  recording is recovered at the next start (ST-020).
- **Windows Installer removes what it installed**: the files, the machine Run value, the shortcut.
- **On uninstall, and never during an upgrade**, the installed service runs once in an uninstall mode,
  as the system account: it stops any running copy, removes the Run values the UI wrote in signed-in
  users' registries, and deletes a technician's data folder (`%LOCALAPPDATA%\ScreenTail`: the store,
  its key, the device's refresh token, models, settings, crash reports) **only while that technician is
  signed in, and with that technician's own token**. The system account deletes nothing inside a profile
  on its own account; a link anywhere between the profile and the data folder leaves it alone. A user
  who is not signed in keeps their folder. The step writes how many folders it deleted and how many it
  left, and why, into the uninstall log.
- So INV-12 holds the way it already did: **"Delete everything" in Settings is how a technician erases
  their data**, and an uninstall is not a substitute for it. The operator doc says this plainly.

### Signing

- **One step, `installer/sign.ps1`**, run on every executable and library in the payload that is not
  already signed (Microsoft's WPF libraries are), on the MSI, and on the setup program and its engine.
  SHA-256 with an RFC 3161 timestamp, so a signature stays valid after its certificate expires, which is
  what `Authenticode.cs` expects. Afterwards every file must verify, carry a timestamp, and share one
  signer, because the peer checks compare signers.
- **Recommended: Azure Artifact Signing** (formerly Trusted Signing): no key to hold, CI signs through
  a service principal, and the certificate chains to a root Windows trusts. A certificate on a hardware
  token or a cloud HSM works too, through the Windows certificate store. No key file is supported: a
  private key in a file is the thing a public repository must never hold.
- **Unconfigured, it signs nothing and says so**, listing what it would have signed. An unsigned build
  is a development build, and the peer checks treat it as one; nothing about them changed.
- In CI the unsigned job runs everywhere; the signed job runs only on main, only when the repository
  variable `SCREENTAIL_SIGNING` is `true`, with its secrets in the `signing` environment.

### The seam for ST-113 (auto-update)

- The UpgradeCode is fixed and every package is a major upgrade, so applying an update is
  `msiexec /i ScreenTail.msi /qn`, and user data survives it (CI checks that).
- One version flows into both executables and the package, so the running service knows what it is.
- **Per machine means an update needs administrator rights**, which neither process has. ST-113 needs
  one of: the MSP's own tool pushing updates (the usual MSP answer); a scheduled task the installer
  registers that runs an elevated updater as the system account; or a small Windows service that only
  updates. It also needs the release feed (ST-007's bucket), a check that a downloaded package is
  signed by the same publisher as the running service (`Authenticode.VerifiedSignerThumbprint` already
  answers that), and "never during a session", which the service knows.

## Consequences

- The ticket's acceptance criteria split three ways. Silent install, start at next sign-in, clean
  uninstall within this ADR's definition, and size are checked by CI's smoke test on every client
  change. "No SmartScreen or antivirus warning on a fresh Windows 11" needs the certificate, and with
  Artifact Signing, reputation that builds over the first downloads; it can only be checked then.
- A technician who is not signed in when an MSP uninstalls keeps their data folder until they use
  "Delete everything" or remove it themselves; a value they wrote for "Start UI at login" stays in their
  registry, naming a program that no longer exists, which Windows passes over.
- The backend address is still read from `SCREENTAIL_BACKEND` only. How an installed client learns it
  (a built-in default, an MSI property, the invite code) is not decided here.

## Alternatives considered

- **A Windows Service for the update and cleanup work.** Would let the uninstall reach every user's
  data at their next sign-in and give ST-113 its elevated updater, at the cost of a privileged process
  running all day. Left for ST-113 to decide.
- **Active Setup for per-user cleanup.** Runs a command as each user at their next sign-in, but leaves a
  machine key behind for ever and would run against a later reinstall's data unless carefully versioned.
- **Shipping the Visual C++ runtime files beside Whisper.** Removes the setup program, but depends on
  the build machine's Visual Studio licence to redistribute them and leaves them out of Windows Update.
