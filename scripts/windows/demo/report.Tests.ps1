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
