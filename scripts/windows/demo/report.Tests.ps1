<#
.SYNOPSIS
    Runs report.ps1's "End-to-end run" section against made-up runner folders and fails if anything the
    report must leave out reaches it, or if what it should show is missing.

.DESCRIPTION
    report.ps1 is dot-sourced with -FunctionsOnly, so its functions are defined and nothing is collected
    or written. Each case makes a folder the way run-e2e.ps1 leaves one (e2e.log, <role>.out.log and
    <role>.err.log, and a file that is not a log), runs the section into the report's body and checks
    the text. No Pester, no network: Windows PowerShell 5.1 and a temp folder.

    Every key, token and secret below is made up at run time from parts, so this file holds none.

    The scripts job runs check-demo-stopped.Tests.ps1, which runs this file. ASCII only.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$reportScript = Join-Path $PSScriptRoot 'report.ps1'
if (-not (Test-Path $reportScript)) { Write-Host "::error::No report at $reportScript."; exit 1 }
. $reportScript -FunctionsOnly
$ErrorActionPreference = 'Stop'

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('report-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

# --- Made-up values, assembled here so the repository holds none of them -----------------------------

$letters = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
function New-Random([int]$Length) {
    return (-join (1..$Length | ForEach-Object { $letters[(Get-Random -Maximum $letters.Length)] }))
}
$googleKey = 'AI' + 'za' + (New-Random 35)
$namedSecret = New-Random 28
$e2eSecret = $script:E2eSecretFirstWords[2] + ('{0:0000}' -f (Get-Random -Maximum 10000)) + $script:E2eSecretSecondWords[3]
$email = 'jane.doe' + '@' + 'example' + '.com'
$userPath = 'C:\Users\' + 'jdoe' + '\AppData\Local\ScreenTail'
$windowTitle = 'Q3 Payroll Budget.xlsx - Excel'
$exceptionMessage = 'The process cannot access the file Board Minutes.docx'
$unhandledMessage = 'Window Salary Review was closed'
$computer = 'HOST' + (New-Random 6).ToUpperInvariant()
$devcertMarker = 'DEVCERT-' + [guid]::NewGuid().ToString('N')
$binaryMarker = 'BINARY-' + [guid]::NewGuid().ToString('N')
$startMarker = 'LEDGER-' + [guid]::NewGuid().ToString('N')
$strayMarker = 'STRAY-' + [guid]::NewGuid().ToString('N')
$olderMarker = 'OLDER-' + [guid]::NewGuid().ToString('N')
# The e2e secret's parts with separators between them, as an OCR or a person might write it.
$e2eDigits = '{0:0000}' -f (Get-Random -Maximum 10000)
$e2eSeparated = $script:E2eSecretFirstWords[0] + ' ' + $e2eDigits + ' ' + $script:E2eSecretSecondWords[0]
$startedAt = (Get-Date).AddMinutes(-3)
$startedText = $startedAt.ToString('yyyy-MM-dd HH:mm:ss', $invariant)
# An exception's message as an IO or parsing error could carry one: a client's folder and a secret-shaped
# value. The client's name is what proves a message stayed out, because no scrub rule removes it.
$client = 'Northwind' + (New-Random 6)
$madePath = 'D:\Clients\' + $client + '\Board Minutes.docx'
$madeSecret = 'gh' + 'p_' + (New-Random 36)
$innerMarker = 'INNER-' + [guid]::NewGuid().ToString('N')
$thrownMessage = "Access to the path '$madePath' is denied; " + 'pass' + "word=$madeSecret"

function Write-Lines([string]$Path, [string[]]$Lines) {
    [System.IO.File]::WriteAllLines($Path, $Lines, $utf8)
}

# A folder as a run that failed two steps leaves it.
function New-RunFolder([string]$Name) {
    $dir = Join-Path $root $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Write-Lines (Join-Path $dir 'e2e.log') @(
        "ScreenTail end-to-end run (ST-013), $startedText on $computer",
        "Backend https://localhost:5071, logs in $userPath, clicks: mark moment over the pipe",
        '',
        "PASS setup-01 windows (0.1 s): waited for Windows PowerShell 5.1. PowerShell 5.1, repository $userPath.",
        "FAIL setup-05 certificate (0.2 s): waited for the certificate. Signed in as $email, key $googleKey",
        '',
        "PASS loop-01 pipe (0.4 s): waited for the pipe. Connected.",
        "FAIL loop-03 test-window (1.0 s): waited for the test window. Foreground was '$windowTitle'. InvalidOperationException: $e2eSecret",
        'FAIL loop: 1 step(s) failed, 1 passed.',
        '',
        "FAIL e2e: 2 step(s) failed, 2 passed. Logs: $userPath"
    )
    # 250 logger lines, so the tail shows the last 200 only.
    $backend = @(1..250 | ForEach-Object { '12:00:00.000 INFO  ScreenTail.Api.Probe: backend line ' + $_.ToString('0000') })
    Write-Lines (Join-Path $dir 'backend.out.log') $backend
    Write-Lines (Join-Path $dir 'backend.err.log') @()
    Write-Lines (Join-Path $dir 'service.out.log') @(
        '12:00:01.000 INFO  ScreenTail.Service.Program: Capture service started',
        "12:00:02.000 INFO  ScreenTail.Service.Summarize: using key $googleKey",
        "12:00:03.000 INFO  ScreenTail.Service.Settings: client_secret=$namedSecret",
        "12:00:04.000 INFO  ScreenTail.Service.Auth: signed in $email",
        "12:00:05.000 INFO  ScreenTail.Service.Store: data in $userPath",
        "12:00:06.000 WARN  ScreenTail.Service.Redaction: a value like $e2eSecret",
        "12:00:06.100 WARN  ScreenTail.Service.Redaction: typed $e2eSeparated end",
        '12:00:06.200 INFO  ScreenTail.Service.Probe: unscrambled tumbleweed Juniper Networks',
        $windowTitle,
        "[start.ps1] Payroll $startMarker",
        '12:00:07.000 ERROR ScreenTail.Service.Store: could not open a file',
        "    System.IO.IOException: $exceptionMessage",
        '    at ScreenTail.Service.Store.Open()',
        "Unhandled exception. System.InvalidOperationException: $unhandledMessage"
    )
    # Not text: a log with a NUL byte in it.
    $bytes = $utf8.GetBytes("12:00:08.000 INFO  ScreenTail.Service.Probe: $binaryMarker")
    [System.IO.File]::WriteAllBytes((Join-Path $dir 'service.err.log'), [byte[]](@($bytes) + @([byte]0) + @($bytes)))
    # Not a log: never read.
    Write-Lines (Join-Path $dir 'devcert.cer') @("-----BEGIN CERTIFICATE-----", $devcertMarker, "-----END CERTIFICATE-----")
    # Logs the runner does not write: never read, only counted.
    Write-Lines (Join-Path $dir 'other.log') @("12:00:00.000 INFO  ScreenTail.Probe: $strayMarker")
    Write-Lines (Join-Path $dir 'backend.1.log') @("12:00:00.000 INFO  ScreenTail.Probe: $olderMarker")
    # From an earlier run: the ui log is older than this run's start.
    $ui = Join-Path $dir 'ui.out.log'
    Write-Lines $ui @('12:00:09.000 INFO  ScreenTail.UI.App: ui line')
    [System.IO.File]::SetLastWriteTime($ui, $startedAt.AddDays(-1))
    return $dir
}

# The section's text, as the report would write it (its notices scrubbed as the report scrubs them).
function Get-Section {
    param([string]$Folder, [string]$Source = $script:DemoLogWriterSource, [string]$Hash = $script:DemoLogWriterHash)
    $script:Body = New-Object System.Collections.Generic.List[string]
    $script:Notices = New-Object System.Collections.Generic.List[string]
    $script:Removed = [ordered]@{}
    Add-E2eReport -Folder $Folder -FilterSource $Source -FilterHash $Hash
    $scrubbed = @($script:Notices | ForEach-Object { Protect-ReportText $_ })
    return [pscustomobject]@{ Lines = @($script:Body); Text = ((@($script:Body) + $scrubbed) -join "`n"); Notices = $scrubbed }
}

# The demo logs section over a made-up logs folder (service.log, backend.log), as the report would write it.
function Get-LogsSection {
    param([string]$Folder, [string]$Source = $script:DemoLogWriterSource, [string]$Hash = $script:DemoLogWriterHash)
    $script:Body = New-Object System.Collections.Generic.List[string]
    $script:Notices = New-Object System.Collections.Generic.List[string]
    $script:Problems = New-Object System.Collections.Generic.List[object]
    $script:Removed = [ordered]@{}
    $logs = @(
        @{ Name = 'Capture service'; Path = (Join-Path $Folder 'service.log'); Rank = 90 },
        @{ Name = 'Backend'; Path = (Join-Path $Folder 'backend.log'); Rank = 92 }
    )
    Add-DemoLogsReport -Logs $logs -OldBackendLog (Join-Path $Folder 'old-backend.log') -CrashDir (Join-Path $Folder 'crashes') -FilterSource $Source -FilterHash $Hash
    $problems = @($script:Problems | ForEach-Object { Protect-ReportText $_.Text })
    return [pscustomobject]@{ Lines = @($script:Body); Text = ((@($script:Body) + $problems) -join "`n") }
}

# One section run through Invoke-Section, and how many kinds of thing the final scrub removed from it.
function Get-ThrowingSection([scriptblock]$Section) {
    $script:Body = New-Object System.Collections.Generic.List[string]
    $script:Removed = [ordered]@{}
    Invoke-Section 'Probe' $Section
    return [pscustomobject]@{ Lines = @($script:Body); Text = (@($script:Body) -join "`n"); Removed = $script:Removed.Count }
}

$script:bad = 0
$script:n = 0
function Check([string]$Name, [bool]$Ok, [string]$Text) {
    $script:n++
    if ($Ok) {
        Write-Host "ok   $Name"
    }
    else {
        $script:bad++
        Write-Host "::error file=scripts/windows/demo/report.ps1::$Name"
        Write-Host $Text
    }
}
function Has([string]$Text, [string]$Part) { return ($Text.IndexOf($Part, [StringComparison]::Ordinal) -ge 0) }
function HasNot([string]$Text, [string]$Part) { return ($Text.IndexOf($Part, [StringComparison]::OrdinalIgnoreCase) -lt 0) }

$link = $null
try {
    # Before anything below loads the filter into a section: report.ps1 itself loads none unchecked.
    Check 'report.ps1 loads no log filter outside a checked reader' ($null -eq (Get-Command ConvertTo-DemoLogLine -ErrorAction SilentlyContinue)) 'ConvertTo-DemoLogLine is defined after dot-sourcing report.ps1 -FunctionsOnly'

    # --- A run that failed --------------------------------------------------------------------------
    $folder = New-RunFolder 'failed-run'
    $section = Get-Section $folder
    $t = $section.Text

    Check 'the section runs without an error' ((Has $t '== End-to-end run') -and (HasNot $t 'could not be checked')) $t
    Check 'a logger line is shown as the logger wrote it' (Has $t '    12:00:01.000 INFO  ScreenTail.Service.Program: Capture service started') $t
    Check 'the summary line is shown' (Has $t 'FAIL e2e: 2 step(s) failed, 2 passed.') $t
    Check 'the driver''s summary is shown' (Has $t 'FAIL loop: 1 step(s) failed, 1 passed.') $t
    Check 'the failed steps are shown by id and status' ((Has $t '    FAIL setup-05 certificate') -and (Has $t '    FAIL loop-03 test-window')) $t
    Check 'a step that passed is not listed' (HasNot $t 'setup-01') $t
    Check 'the run''s start time is shown' (Has $t $startedText) $t
    Check 'what a failed step printed is not shown' ((HasNot $t 'waited for the certificate') -and (HasNot $t 'Foreground was')) $t
    Check 'a failed run is named at the top' (@($section.Notices | Where-Object { $_ -like '*end-to-end run*failed*FAIL setup-05 certificate*' }).Count -eq 1) $t

    Check 'a Google-shaped key is removed' ((HasNot $t $googleKey) -and (Has $t 'using key [key removed]')) $t
    Check 'a secret written as name=value is removed' ((HasNot $t $namedSecret) -and (Has $t 'client_secret=[removed]')) $t
    Check 'the end-to-end run''s secret is removed by its shape' ((HasNot $t $e2eSecret) -and (Has $t 'a value like [e2e secret removed]')) $t
    Check 'an email address is removed' ((HasNot $t $email) -and (Has $t 'signed in [email]')) $t
    Check 'a user folder path is shown as %USERPROFILE%' ((HasNot $t 'jdoe') -and (Has $t 'data in %USERPROFILE%\AppData\Local\ScreenTail')) $t
    Check 'a title-like line not from the logger is withheld' ((HasNot $t 'Payroll') -and (Has $t "[line not from ScreenTail's logger withheld, $($windowTitle.Length) chars]")) $t
    Check 'an exception''s message is withheld, its type and frame kept' ((HasNot $t 'Board Minutes') -and (Has $t '    System.IO.IOException: [message withheld]') -and (Has $t 'at ScreenTail.Service.Store.Open()')) $t
    Check 'an unhandled exception keeps only its type' ((HasNot $t 'Salary Review') -and (Has $t 'Unhandled exception. System.InvalidOperationException [message and stack withheld]')) $t
    Check 'the computer name in e2e.log is not shown' (HasNot $t $computer) $t
    Check 'a file that is not a .log is not read, only counted' ((HasNot $t $devcertMarker) -and (HasNot $t 'BEGIN CERTIFICATE') -and ($t -match 'Other files in the folder \(not read\) +3(\n|$)')) $t
    Check 'a log the runner does not write (other.log, backend.1.log) is not read' ((HasNot $t $strayMarker) -and (HasNot $t $olderMarker)) $t
    Check 'a raw [start.ps1] line from a process is withheld' ((HasNot $t $startMarker) -and (Has $t "[line not from ScreenTail's logger withheld, $(('[start.ps1] Payroll ' + $startMarker).Length) chars]")) $t
    Check 'the e2e secret with separators is removed, digits too' ((HasNot $t $e2eSeparated) -and (Has $t 'typed [e2e secret removed] end')) $t
    Check 'words that only contain the secret''s words are kept' (Has $t 'ScreenTail.Service.Probe: unscrambled tumbleweed Juniper Networks') $t
    Check 'a log that is not text is not read' ((HasNot $t $binaryMarker) -and (Has $t 'service.err.log: not text, so not read')) $t
    Check 'the tail is as long as the demo logs''' ((Has $t 'backend.out.log: 250 lines') -and (Has $t "The last $($script:LogTail):") -and (Has $t 'backend line 0051') -and (HasNot $t 'backend line 0050')) $t
    Check 'an empty log says so' (Has $t 'backend.err.log: empty') $t
    Check 'a log older than the run says so' ((Has $t 'ui.out.log: 1 lines, last written') -and (Has $t 'from an earlier run')) $t
    Check 'the driver points at e2e.log' (Has $t 'the runner writes the driver''s lines into e2e.log') $t

    # --- A log over the size cap is read from its end only ------------------------------------------
    $big = Join-Path $root 'big-run'
    New-Item -ItemType Directory -Force -Path $big | Out-Null
    $line = '12:00:00.000 INFO  ScreenTail.Service.Probe: ' + ('x' * 60) + ' '
    $count = [int]([Math]::Ceiling(($script:DemoLogCapBytes + 200000) / ($line.Length + 6)))
    Write-Lines (Join-Path $big 'service.out.log') @(1..$count | ForEach-Object { $line + $_.ToString('000000') })
    $t = (Get-Section $big).Text
    Check 'a log over 2 MB is read from its last 2 MB' ((Has $t 'lines in its last 2 MB') -and (Has $t ('x ' + $count.ToString('000000')))) $t
    Check 'a folder without e2e.log says so' (Has $t 'not there (the last run stopped before writing it)') $t

    # --- No folder, no section ----------------------------------------------------------------------
    $missing = Get-Section (Join-Path $root 'no-such-folder')
    Check 'a missing folder adds no section' ($missing.Lines.Count -eq 0) ($missing.Lines -join "`n")

    # --- A filter that cannot be checked shows nothing ----------------------------------------------
    $changed = Get-Section $folder -Source ($script:DemoLogWriterSource + "`n# changed") -Hash $script:DemoLogWriterHash
    $t = $changed.Text
    Check 'a filter that fails its hash shows nothing from the run' ((Has $t 'The log filter could not be checked, so nothing from the end-to-end run is shown.') -and (HasNot $t 'Capture service started') -and (HasNot $t 'FAIL e2e') -and (HasNot $t 'backend line')) $t

    $none = Get-Section $folder -Source '' -Hash $script:DemoLogWriterHash
    $t = $none.Text
    Check 'a missing filter shows nothing from the run' ((Has $t 'The log filter could not be checked') -and (HasNot $t 'Capture service started') -and (HasNot $t 'FAIL e2e')) $t

    $other = 'function Get-NothingUseful { return 1 }'
    $t = (Get-Section $folder -Source $other -Hash (Get-DemoLogWriterHash $other)).Text
    Check 'a filter text that does not define ConvertTo-DemoLogLine is refused' ((Has $t 'The log filter could not be checked') -and (HasNot $t 'Capture service started')) $t

    $weak = 'function ConvertTo-DemoLogLine([string]$Line, [hashtable]$State) { return $Line }'
    $open = Get-Section $folder -Source $weak -Hash (Get-DemoLogWriterHash $weak)
    $t = $open.Text
    Check 'a filter that keeps everything is refused, even with its own hash' ((Has $t 'The log filter could not be checked') -and (HasNot $t 'Payroll') -and (HasNot $t 'Capture service started')) $t

    # --- A junction for the folder is not followed --------------------------------------------------
    $target = New-RunFolder 'junction-target'
    $link = Join-Path $root 'junction-link'
    & cmd.exe /c mklink /J "$link" "$target" | Out-Null
    Check 'the junction for the test was made' (Test-Path -LiteralPath $link) $link
    $t = (Get-Section $link).Text
    Check 'a folder that is a junction is not followed' ((Has $t 'The folder is a link to another place, so nothing in it was read.') -and (HasNot $t 'Capture service started') -and (HasNot $t 'FAIL e2e')) $t

    # --- A log that cannot be opened says so, with no exception message -----------------------------
    $locked = Join-Path $root 'locked-run'
    New-Item -ItemType Directory -Force -Path $locked | Out-Null
    $lockedLog = Join-Path $locked 'service.out.log'
    Write-Lines $lockedLog @('12:00:00.000 INFO  ScreenTail.Service.Program: locked line')
    $hold = New-Object System.IO.FileStream($lockedLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
    try { $t = (Get-Section $locked).Text } finally { $hold.Dispose() }
    Check 'a log that cannot be opened says so in a fixed sentence' ((Has $t 'service.out.log: could not be read') -and (HasNot $t 'locked line') -and (HasNot $t 'being used by another process') -and (HasNot $t 'could not be checked:')) $t

    # --- A program's output read directly (the capability check) ------------------------------------
    $output = "[start.ps1] Payroll $startMarker`r`nUnhandled exception. System.Exception: $unhandledMessage`n12:00:00.000 INFO  ScreenTail.Service.Program: capabilities checked"
    $t = @(Select-ProgramOutputLines $output) -join "`n"
    Check 'a program''s raw [start.ps1] line is withheld' ((HasNot $t $startMarker) -and (Has $t "[line not from ScreenTail's logger withheld,")) $t
    Check 'a program''s output keeps logger lines and an exception''s type only' ((Has $t 'capabilities checked') -and (HasNot $t 'Salary Review') -and (Has $t 'Unhandled exception. System.Exception [message and stack withheld]')) $t
    $weakHash = Get-DemoLogWriterHash $weak
    $t = @(Select-ProgramOutputLines -Text $output -FilterSource $weak -FilterHash $weakHash) -join "`n"
    Check 'a program''s output is not shown through a filter that keeps everything' ((Has $t '[The log filter could not be checked, so this output is not shown.]') -and (HasNot $t 'capabilities checked') -and (HasNot $t 'Salary Review') -and (HasNot $t $startMarker)) $t

    # --- A section that throws: a fixed sentence and the exception's type, never its text -------------
    $pattern = '^  The Probe section could not be completed \([A-Za-z_][A-Za-z0-9_.]*\)\.$'
    $thrown = Get-ThrowingSection { throw (New-Object System.UnauthorizedAccessException($thrownMessage, (New-Object System.IO.IOException("$innerMarker $madePath")))) }
    $t = $thrown.Text
    Check 'a section that throws says so in a fixed sentence with the type' (($thrown.Lines.Count -eq 3) -and ($thrown.Lines[1] -eq '== Probe') -and ($thrown.Lines[2] -ceq '  The Probe section could not be completed (System.UnauthorizedAccessException).')) $t
    Check 'a section that throws shows no message, path, secret or inner exception' ((HasNot $t $client) -and (HasNot $t 'Board Minutes') -and (HasNot $t $madeSecret) -and (HasNot $t $innerMarker) -and (HasNot $t 'denied') -and (HasNot $t 'could not be checked:')) $t
    Check 'the final scrub had nothing to remove from it, so the message never reached it' ($thrown.Removed -eq 0) $t

    $t = (Get-ThrowingSection { throw $thrownMessage }).Text
    Check 'a thrown string shows only its type' (((($t -split "`n")[2]) -cmatch $pattern) -and (HasNot $t $client) -and (HasNot $t 'denied')) $t
    $t = (Get-ThrowingSection { Write-Error -Message $thrownMessage -ErrorAction Stop }).Text
    Check 'an error written with Write-Error shows only its type' (((($t -split "`n")[2]) -cmatch $pattern) -and (HasNot $t $client) -and (HasNot $t 'denied')) $t
    $t = (Get-ThrowingSection { $null = [System.IO.File]::ReadAllText($madePath) }).Text
    Check 'a .NET method that fails on a path shows only its type' (((($t -split "`n")[2]) -cmatch $pattern) -and (HasNot $t $client) -and (HasNot $t 'Exception calling') -and (HasNot $t 'Could not find')) $t
    $t = (Get-ThrowingSection { $null = ('{"' + $client + '": ') | ConvertFrom-Json }).Text
    Check 'a parse error shows only its type' (((($t -split "`n")[2]) -cmatch $pattern) -and (HasNot $t $client)) $t
    Check 'something that is not an exception has no type to show' ((Get-ExceptionTypeName $thrownMessage) -ceq 'an unknown error') (Get-ExceptionTypeName $thrownMessage)

    # --- The capability check, when the capture service cannot be started ---------------------------
    $script:Body = New-Object System.Collections.Generic.List[string]
    $script:Problems = New-Object System.Collections.Generic.List[object]
    Add-CapabilityCheck -Exe (Join-Path $root ($client + '.exe')) -WorkingDirectory $root -Seconds 5
    $t = @($script:Body) -join "`n"
    Check 'a capability check that cannot run says so with the type only' (($t -cmatch 'Capability check +could not run \([A-Za-z_][A-Za-z0-9_.]*\)') -and (HasNot $t $client) -and (HasNot $t 'cannot find') -and (HasNot $t 'Exception calling')) $t

    # --- Why a demo window's program stopped (backend.exited, service.exited) -----------------------
    $exited = Join-Path $root 'exited'
    New-Item -ItemType Directory -Force -Path $exited | Out-Null
    Write-Lines (Join-Path $exited 'code.exited') @('exit code 3')
    Write-Lines (Join-Path $exited 'message.exited') @($thrownMessage)
    Write-Lines (Join-Path $exited 'blocked.exited') @("Program '$madePath' failed to run: An Application Control policy has blocked this file")
    $reason = Get-ExitedReason (Join-Path $exited 'code.exited')
    Check 'an exit code is shown as start wrote it' (($reason.Text -ceq 'exit code 3') -and -not $reason.Blocked) $reason.Text
    $reason = Get-ExitedReason (Join-Path $exited 'message.exited')
    Check 'an error message start wrote is not shown' ((HasNot $reason.Text $client) -and (HasNot $reason.Text 'denied') -and (HasNot $reason.Text $madeSecret) -and -not $reason.Blocked) $reason.Text
    $reason = Get-ExitedReason (Join-Path $exited 'blocked.exited')
    Check 'a Windows block is still recognised, its message not shown' ($reason.Blocked -and (HasNot $reason.Text $client) -and (HasNot $reason.Text 'Application Control')) $reason.Text

    # --- A program's error output (git, dotnet) is never used -----------------------------------------
    $native = Invoke-Native -FilePath 'cmd.exe' -ArgumentList @('/c', "echo out-$client& echo err-$client 1>&2")
    $t = 'Lines: ' + (@($native.Lines) -join ' | ') + "`nOut: " + (@($native.Out) -join ' | ')
    Check 'a program''s output is kept apart from its error output' ((Has (@($native.Lines) -join ' ') "err-$client") -and (Has (@($native.Out) -join ' ') "out-$client") -and (HasNot (@($native.Out) -join ' ') "err-$client")) $t
    Check 'a program that succeeded gives its output only' ((@(Get-NativeOutput $native) -join ' ') -match "^out-$client\s*$") $t
    $failed = Invoke-Native -FilePath 'cmd.exe' -ArgumentList @('/c', "echo fatal: unsafe repository C:/Users/$client/src 1>&2 & exit /b 128")
    $t = 'Exit: ' + $failed.ExitCode + "`nLines: " + (@($failed.Lines) -join ' | ')
    Check 'a program that failed gives nothing, not its error' (($failed.ExitCode -eq 128) -and (@(Get-NativeOutput $failed).Count -eq 0)) $t

    # --- The demo logs section: the checked filter, and start's own lines rebuilt ---------------------
    $logsDir = Join-Path $root 'demo-logs'
    New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
    Write-Lines (Join-Path $logsDir 'service.log') @(
        '12:00:01.000 INFO  ScreenTail.Service.Program: demo service started',
        $windowTitle,
        '[start.ps1] The capture service has stopped (exit code 3).',
        "[start.ps1] The capture service could not run: $thrownMessage",
        "[start.ps1] The capture service has stopped ($thrownMessage).",
        "[start.ps1] Payroll $startMarker",
        '12:00:07.000 ERROR ScreenTail.Service.Store: could not open a file',
        "    System.IO.IOException: $exceptionMessage",
        '    at ScreenTail.Service.Store.Open()'
    )
    Write-Lines (Join-Path $logsDir 'backend.log') @(
        '12:00:02.000 INFO  ScreenTail.Api.Program: demo backend started',
        "[start.ps1] The backend could not run: $thrownMessage"
    )
    $t = (Get-LogsSection $logsDir).Text
    Check 'the demo logs section runs through the checked filter' ((Has $t '== Logs') -and (Has $t '    12:00:01.000 INFO  ScreenTail.Service.Program: demo service started') -and (Has $t 'demo backend started') -and (HasNot $t 'could not be checked') -and (HasNot $t 'could not be completed')) $t
    Check 'a demo log''s title-like line is withheld' ((HasNot $t 'Payroll') -and (Has $t "[line not from ScreenTail's logger withheld, $($windowTitle.Length) chars]")) $t
    Check 'a demo log''s exception message is withheld, its type kept' ((HasNot $t 'Board Minutes') -and (Has $t '    System.IO.IOException: [message withheld]')) $t
    Check 'start''s exit code line is kept as it is' (Has $t '    [start.ps1] The capture service has stopped (exit code 3).') $t
    Check 'start''s lines with an error message keep only that there was one' ((Has $t '[start.ps1] The capture service could not run [reason withheld]') -and (Has $t '[start.ps1] The capture service has stopped [reason withheld]') -and (Has $t '[start.ps1] The backend could not run [reason withheld]') -and (HasNot $t $client) -and (HasNot $t 'denied')) $t
    Check 'any other [start.ps1] line in a demo log is withheld' (HasNot $t $startMarker) $t
    Check 'the reasons withheld from start''s lines are counted for the top of the report' ([int]$script:Removed['reasons in start.cmd''s own log lines (an error''s message)'] -eq 3) (($script:Removed.Keys | ForEach-Object { "$_ = $($script:Removed[$_])" }) -join "`n")
    Check 'an error the log has still reaches the top' (Has $t 'The capture service logged 1 error(s). The first: 12:00:07.000 ERROR ScreenTail.Service.Store: could not open a file') $t

    $t = (Get-LogsSection $logsDir -Source $weak -Hash $weakHash).Text
    Check 'the demo logs section refuses a filter that keeps everything' ((Has $t 'The log filter could not be checked, so no log is shown.') -and (HasNot $t 'demo service started') -and (HasNot $t 'Payroll') -and (HasNot $t $client) -and (HasNot $t 'logged 1 error')) $t

    $changedText = Get-LogsSection $logsDir -Source ($script:DemoLogWriterSource + "`n# changed") -Hash $script:DemoLogWriterHash
    Check 'the demo logs section refuses a filter that fails its hash' ((Has $changedText.Text 'The log filter could not be checked, so no log is shown.') -and (HasNot $changedText.Text 'demo service started')) $changedText.Text

    # The filter as it was: every [start.ps1] line read back kept whole. Refused by every reader.
    $reading = 'if ($State.Reading) {'
    $keepsStart = $script:DemoLogWriterSource.Replace($reading, $reading + "`n        if (`$Line.StartsWith('[start.ps1] ', [StringComparison]::Ordinal)) { return `$Line }")
    Check 'the test filter that keeps start''s lines was made' (-not [string]::Equals($keepsStart, $script:DemoLogWriterSource, [StringComparison]::Ordinal)) $reading
    $keepsHash = Get-DemoLogWriterHash $keepsStart
    $t = (Get-LogsSection $logsDir -Source $keepsStart -Hash $keepsHash).Text
    Check 'the demo logs section refuses a filter that keeps start''s lines whole' ((Has $t 'The log filter could not be checked, so no log is shown.') -and (HasNot $t $client)) $t
    $t = (Get-Section $folder -Source $keepsStart -Hash $keepsHash).Text
    Check 'the end-to-end section refuses a filter that keeps start''s lines whole' ((Has $t 'The log filter could not be checked, so nothing from the end-to-end run is shown.') -and (HasNot $t 'Capture service started')) $t
}
catch {
    $script:bad++
    Write-Host "::error file=scripts/windows/demo/report.Tests.ps1::The cases stopped: $($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"
}
finally {
    # The junction first, by rmdir, which removes the link and never what it points to.
    if ($link -and (Test-Path -LiteralPath $link)) { & cmd.exe /c rmdir "$link" | Out-Null }
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}

if ($script:bad -gt 0) {
    Write-Host "$script:bad of $script:n report cases gave the wrong answer."
    exit 1
}
Write-Host "All $script:n report cases gave the right answer."
exit 0
