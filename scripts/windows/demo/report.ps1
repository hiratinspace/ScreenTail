<#
.SYNOPSIS
    Writes one plain-text report about the ScreenTail demo on this laptop to the Desktop (or, when the
    Desktop is synced to OneDrive, to %LOCALAPPDATA%\ScreenTail.Dev\demo\reports), for sending to the
    developer.

.DESCRIPTION
    Run it after something did not work, while the demo is still running if it is. It answers, without
    you doing anything else: what this machine and the clone are, which parts of the demo are running,
    whether the backend answers, the settings the demo depends on (yes/no or names only), what Windows
    lets the capture service do, the capture settings and hotkeys, and the last 200 lines of each log.
    When scripts\windows\e2e\run-e2e.cmd has run on this laptop, it also says how the last end-to-end run
    went: its result, when it ran, the ids of the steps that failed and the last 200 lines of each
    process's log. At the top it says what looks wrong first, with a one-line fix.

    It only reads. It starts nothing that stays running, stops nothing and saves nothing except the
    report itself. The one program it runs is the capture service's own "--capabilities" check, which
    asks Windows what it allows and exits.

    It is made to leave out screenshots, OCR text, transcripts, note text, window titles, keys, tokens,
    passwords and connection strings. Of the logs it keeps only the lines ScreenTail's scrubbing log
    sink wrote (INV-10): any other line a program printed (a .NET crash message, native library output)
    is withheld and shown only as its length, and of an unhandled exception only the type is kept. Every
    line of the report then goes through a final pattern scrub. The top of the report says what was
    withheld and removed.

.PARAMETER FunctionsOnly
    Defines the report's functions and stops, collecting and writing nothing. For report.Tests.ps1, which
    dot-sources this file to run the end-to-end section against made-up folders.

.EXAMPLE
    scripts\windows\demo\report.cmd
#>
[CmdletBinding()]
param(
    [switch]$FunctionsOnly
)

. (Join-Path $PSScriptRoot 'common.ps1')

$script:Body = New-Object System.Collections.Generic.List[string]
$script:Problems = New-Object System.Collections.Generic.List[object]
$script:Notices = New-Object System.Collections.Generic.List[string]
$script:Removed = [ordered]@{}
$script:KnownSecrets = @()
$script:Secrets = @{}
$script:States = @{}
$script:Capture = $null
$script:LogTail = 200

# --- The final scrub ------------------------------------------------------------------------------------

# Each rule: what it is called at the top of the report, the pattern, and what a match becomes. Broad on
# purpose; a mangled line costs nothing, a sent key costs a key. A value that already starts with "[" was
# replaced by an earlier rule and is left as it is.
#
# Every name rule starts with a lookbehind, so a name is only tried where one can begin. Without it a
# long run of name characters is tried from every position, which is quadratic. Lines over
# $script:LineCut characters are not scrubbed at all but replaced by their length, before any rule runs.
#
# Names written "name: value" are only matched where the name does not follow a dot, because the logger
# writes "Namespace.Category: message" and a category such as ...TokenIssuer is not a secret's name. Known
# gaps, accepted: a dotted name written with a colon ("user.password: x") is not matched, and a JSON value
# on the line after its name is not matched, because each line is scrubbed on its own. The exact-value
# rule above them still catches the saved secrets in either case.
#
# A "name: value" secret stops at "@", so a token in an address (https://x-access-token:...@host) loses
# the token and keeps the host.
#
# The end-to-end run's fake secret is made up for each run and never saved, so it cannot be found by its
# value. The last rule removes it by its shape (common.ps1's word lists): a word and digits, digits and a
# word, or two words, with at most one space, dot, underscore or hyphen between the parts, so the digits go
# too. The match may not touch a letter on either side, so a word that only contains one of the words
# ("unscrambled", "tumbleweed") is kept, and so is a word on its own ("Juniper Networks").
$script:LineCut = 4000
$passwordNames = 'password|passwd|pwd|psw'
$secretNames = 'secret|token|credential|api[_\-]?key|signing[_\-]?key|access[_\-]?key|master[_\-]?key|private[_\-]?key|public[_\-]?key|client[_\-]?secret|device[_\-]?token|refresh[_\-]?token'
$quoted = '"(?:[^"\\]|\\.)*"|''[^'']*'''
$nameStart = '(?<![A-Za-z0-9_.:\-])'
$secretWord = '(?:' + ((@($script:E2eSecretFirstWords) + @($script:E2eSecretSecondWords)) -join '|') + ')'
$secretDigits = '[0-9]{1,6}'
$secretGap = '[\s._-]?'
$script:ScrubRules = @(
    @{ Label = 'authorization headers'; Pattern = '(?i)\b((?:proxy-)?authorization)("?\s*[:=]\s*"?)(?:(?:basic|bearer|digest|token)\s+)?(?!\[)[^\s"]+'; Replacement = '$1$2[removed]' },
    @{ Label = 'key headers'; Pattern = '(?i)\b(x-goog-api-key|x-api-key|api-key|x-auth-token)(\s*[:=]\s*)(?!\[)\S+'; Replacement = '$1$2[removed]' },
    @{ Label = 'JWT tokens'; Pattern = 'eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+'; Replacement = '[token removed]' },
    @{ Label = 'bearer tokens'; Pattern = '(?i)\bbearer\s+(?!\[)[A-Za-z0-9\-._~+/]+=*'; Replacement = 'Bearer [removed]' },
    @{ Label = 'Google API keys (AIza...)'; Pattern = 'AIza[0-9A-Za-z_\-]{20,}'; Replacement = '[key removed]' },
    @{ Label = 'API keys (sk-...)'; Pattern = '\bsk-[A-Za-z0-9_\-]{16,}'; Replacement = '[key removed]' },
    @{ Label = 'passwords written as name=value'; Pattern = "(?i)$nameStart([A-Za-z0-9_.:\-]*(?:$passwordNames)[A-Za-z0-9_\-]*)(\s*=\s*)(?!\[)($quoted|[^;\r\n]+)"; Replacement = '$1$2[removed]' },
    @{ Label = 'passwords written as name: value'; Pattern = "(?i)$nameStart([A-Za-z0-9_:\-]*(?:$passwordNames)[A-Za-z0-9_\-]*)(\s*:\s*)(?!\[)($quoted|[^;\r\n]+)"; Replacement = '$1$2[removed]' },
    @{ Label = 'secrets, keys and tokens written as name=value'; Pattern = "(?i)$nameStart([A-Za-z0-9_.:\-]*(?:$secretNames)[A-Za-z0-9_\-]*)(\s*=\s*)(?!\[)($quoted|[^\s;,&`"']+)"; Replacement = '$1$2[removed]' },
    @{ Label = 'secrets, keys and tokens written as name: value'; Pattern = "(?i)$nameStart([A-Za-z0-9_:\-]*(?:$secretNames)[A-Za-z0-9_\-]*)(\s*:\s*)(?!\[)($quoted|[^\s;,&`"'@]+)"; Replacement = '$1$2[removed]' },
    @{ Label = 'secrets written as "name": "value"'; Pattern = "(?i)(`"[^`"]*(?:$passwordNames|$secretNames|connectionstring|postgres)[^`"]*`"\s*:\s*)(?!`"\[)(?:`"(?:[^`"\\]|\\.)*`")"; Replacement = '$1"[removed]"' },
    @{ Label = 'passwords in connection addresses'; Pattern = '(?i)(?<![a-z0-9+.\-])([a-z][a-z0-9+.\-]*://[^:/@\s]+:)(?!\[)[^@/\s]+@'; Replacement = '$1[removed]@' },
    @{ Label = 'email addresses'; Pattern = '(?<![A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}'; Replacement = '[email]' },
    @{ Label = 'the end-to-end run''s made-up secret, by its shape'; Pattern = "(?i)(?<![A-Za-z])(?:$secretWord$secretGap$secretDigits(?:$secretGap$secretWord)?|$secretDigits$secretGap$secretWord|$secretWord$secretGap$secretWord)(?![A-Za-z])"; Replacement = '[e2e secret removed]' }
)

# A run of 32 or more key-like characters with upper case, lower case and digits in it: a key or a token
# nobody named. A git commit (lower-case hex) and a session id (a GUID) do not match.
$script:BlobPattern = '[A-Za-z0-9+/_\-]{32,}={0,2}'

function Add-Removed([string]$Label, [int]$Count = 1) {
    $script:Removed[$Label] = [int]$script:Removed[$Label] + $Count
}

function Protect-ReportText([string]$Text) {
    if ($null -eq $Text) { return '' }
    if ($Text.Length -gt $script:LineCut) {
        Add-Removed "lines over $script:LineCut characters (shown only as their length)"
        return "[$($Text.Length) chars, cut]"
    }
    $clean = $Text

    foreach ($secret in $script:KnownSecrets) {
        if ($clean.IndexOf($secret, [StringComparison]::Ordinal) -ge 0) {
            $clean = $clean.Replace($secret, '[secret removed]')
            Add-Removed 'saved secrets, found by their exact value'
        }
    }

    foreach ($rule in $script:ScrubRules) {
        $found = [regex]::Matches($clean, $rule.Pattern)
        if ($found.Count -gt 0) {
            $clean = [regex]::Replace($clean, $rule.Pattern, $rule.Replacement)
            Add-Removed $rule.Label $found.Count
        }
    }

    foreach ($match in @([regex]::Matches($clean, $script:BlobPattern))) {
        $value = $match.Value
        if ($value -cmatch '[A-Z]' -and $value -cmatch '[a-z]' -and $value -match '[0-9]') {
            $clean = $clean.Replace($value, '[long key-like string removed]')
            Add-Removed 'long key-like strings'
        }
    }

    # Paths and names that say whose laptop this is.
    if ($env:USERPROFILE) {
        $pattern = '(?i)' + [regex]::Escape($env:USERPROFILE)
        $found = [regex]::Matches($clean, $pattern)
        if ($found.Count -gt 0) {
            $clean = [regex]::Replace($clean, $pattern, '%USERPROFILE%')
            Add-Removed 'user folder paths (shown as %USERPROFILE%)' $found.Count
        }
    }
    $found = [regex]::Matches($clean, '(?i)\b[A-Z]:\\Users\\[^\\\s"'']+')
    if ($found.Count -gt 0) {
        $clean = [regex]::Replace($clean, '(?i)\b[A-Z]:\\Users\\[^\\\s"'']+', '%USERPROFILE%')
        Add-Removed 'user folder paths (shown as %USERPROFILE%)' $found.Count
    }
    if ($env:USERNAME -and $env:USERNAME.Length -ge 4) {
        $pattern = '(?i)(?<![A-Za-z0-9])' + [regex]::Escape($env:USERNAME) + '(?![A-Za-z0-9])'
        $found = [regex]::Matches($clean, $pattern)
        if ($found.Count -gt 0) {
            $clean = [regex]::Replace($clean, $pattern, '%USERNAME%')
            Add-Removed 'the Windows user name (shown as %USERNAME%)' $found.Count
        }
    }
    if ($env:COMPUTERNAME -and $env:COMPUTERNAME.Length -ge 4) {
        $pattern = '(?i)(?<![A-Za-z0-9])' + [regex]::Escape($env:COMPUTERNAME) + '(?![A-Za-z0-9])'
        $found = [regex]::Matches($clean, $pattern)
        if ($found.Count -gt 0) {
            $clean = [regex]::Replace($clean, $pattern, '%COMPUTERNAME%')
            Add-Removed 'the computer name (shown as %COMPUTERNAME%)' $found.Count
        }
    }
    return $clean
}

# --- Writing the report ---------------------------------------------------------------------------------

function Add-Line([string]$Text = '') {
    $script:Body.Add((Protect-ReportText $Text))
}

function Add-Fact([string]$Label, $Value) {
    Add-Line ('  ' + $Label.PadRight(40) + ' ' + [string]$Value)
}

# A problem for the top of the report. The lowest rank found is the one shown first.
function Add-Problem([int]$Rank, [string]$Text, [string]$Fix) {
    $script:Problems.Add([pscustomobject]@{ Rank = $Rank; Order = $script:Problems.Count; Text = $Text; Fix = $Fix })
}

function Add-Notice([string]$Text) {
    $script:Notices.Add($Text)
}

# The type of what a catch block caught (an error record or an exception), for a fixed sentence. Never its
# message, its stack or an inner exception: an IO or parsing error's message can carry a path, a value or
# a key's name, and the report never carries exception text. A type whose full name is not a plain dotted
# name (a generic type's lists assemblies) is given by its short name.
function Get-ExceptionTypeName($Caught) {
    $exception = $Caught
    if ($Caught -is [System.Management.Automation.ErrorRecord]) { $exception = $Caught.Exception }
    if (-not ($exception -is [Exception])) { return 'an unknown error' }
    $type = $exception.GetType()
    foreach ($name in @($type.FullName, $type.Name)) {
        if ($name -and $name -cmatch '^[A-Za-z_][A-Za-z0-9_.]{0,200}$') { return $name }
    }
    return 'an unknown error'
}

# Runs one section. A section that fails says so in the report, in a fixed sentence with the exception's
# type only, and the rest still runs.
function Invoke-Section([string]$Title, [scriptblock]$Body) {
    Write-Host "  $Title"
    Add-Line ''
    Add-Line ('== ' + $Title)
    try {
        & $Body
    }
    catch {
        Add-Line ('  The ' + $Title + ' section could not be completed (' + (Get-ExceptionTypeName $_) + ').')
    }
}

function Get-YesNo($Value) {
    if ($Value) { return 'yes' }
    return 'no'
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $raw = [IO.File]::ReadAllText($Path)
        if (-not $raw.Trim()) { return $null }
        return ($raw | ConvertFrom-Json)
    }
    catch {
        return 'unreadable'
    }
}

function Get-FileTime([string]$Path) {
    return [IO.File]::GetLastWriteTime($Path).ToString('yyyy-MM-dd HH:mm')
}

# The user secrets, without stopping on a missing or broken file the way the start scripts do.
function Read-SecretsQuietly {
    $result = [pscustomobject]@{ Path = $null; Table = @{}; Problem = $null }
    $project = [IO.File]::ReadAllText($script:DemoApiProject)
    $match = [regex]::Match($project, '<UserSecretsId>\s*([^<\s]+)\s*</UserSecretsId>')
    if (-not $match.Success) {
        $result.Problem = 'the API project names no UserSecretsId'
        return $result
    }
    $result.Path = Join-Path $env:APPDATA ('Microsoft\UserSecrets\' + $match.Groups[1].Value + '\secrets.json')
    if (-not (Test-Path -LiteralPath $result.Path)) {
        $result.Problem = 'missing'
        return $result
    }
    try {
        $raw = [IO.File]::ReadAllText($result.Path)
        if ($raw.Trim()) {
            Add-FlatEntries -Node ($raw | ConvertFrom-Json) -Prefix '' -Into $result.Table
        }
    }
    catch {
        $result.Problem = 'not valid JSON'
    }
    return $result
}

# Runs a program with a time limit and returns its exit code and output. Only for the capture service's
# --capabilities check, which must not be able to hold the report up.
function Invoke-WithTimeout {
    param([string]$FilePath, [string]$Arguments, [string]$WorkingDirectory, [int]$Seconds = 60)

    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $FilePath
    $info.Arguments = $Arguments
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($info)
    $out = $process.StandardOutput.ReadToEndAsync()
    $err = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($Seconds * 1000)) {
        try { $process.Kill() } catch { }
        return [pscustomobject]@{ ExitCode = -1; Out = ''; Err = ''; TimedOut = $true }
    }
    $process.WaitForExit()
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Out = $out.Result; Err = $err.Result; TimedOut = $false }
}

# Lines read from a log or a program's output, with every line that ScreenTail's scrubbing logger did not
# write withheld, the same way the windows withhold them as they write. Again here because a file from an
# older start, or a program's output read directly, was never filtered.
#
# It uses ConvertTo-DemoLogLine, the filter start's windows write their logs through, which this file never
# loads on its own: each reader loads it with Get-CheckedLogFilter first (the demo logs section, the
# end-to-end section, Select-ProgramOutputLines), so a filter that keeps everything is refused by all of
# them. Called with no filter loaded, it fails, and its section says so.
#
# Without -Raw the lines are read as a file the windows' log writer wrote, which also keeps the lines that
# writer adds itself ("[start.ps1] ...", its withheld and dropped notes). -Raw is for anything that writer
# did not write (a program's output, the end-to-end runner's logs): there those lines came from a process
# and are withheld like any other, as the windows withhold them as they write.
function Select-DemoLogLines([string[]]$Lines, [switch]$Raw) {
    $state = @{ InLogger = $false; Reading = (-not $Raw) }
    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($line in $Lines) {
        $text = ConvertTo-DemoLogLine $line $state
        if ($text -match '^\[line not from ScreenTail''s logger withheld, \d+ chars\]$|^Unhandled exception\. \S+ \[message and stack withheld\]$') {
            Add-Removed 'lines not written by ScreenTail''s logger (shown only as their length, or an exception''s type)'
        }
        elseif ($text -cmatch '^\[start\.ps1\] .* \[reason withheld\]$' -and -not [string]::Equals($text, [string]$line, [StringComparison]::Ordinal)) {
            Add-Removed 'reasons in start.cmd''s own log lines (an error''s message)'
        }
        $kept.Add($text)
    }
    return $kept.ToArray()
}

# A program's output read directly (the capture service's --capabilities check), split into its
# non-empty lines and filtered as raw output, through the checked filter. If the filter cannot be
# checked, one fixed line instead of the output.
function Select-ProgramOutputLines {
    param([string]$Text, [string]$FilterSource = $script:DemoLogWriterSource, [string]$FilterHash = $script:DemoLogWriterHash)

    $checked = Get-CheckedLogFilter -Source $FilterSource -Hash $FilterHash
    if (-not $checked) { return @('[The log filter could not be checked, so this output is not shown.]' + (Get-LanguageModeNote)) }
    # Loaded into this function's own scope, where Select-DemoLogLines finds it.
    . $checked
    $lines = @(([string]$Text -split '\r\n|\r|\n') | Where-Object { $_.Trim() })
    return @(Select-DemoLogLines -Raw $lines)
}

# Whether a key looks like the drafting company's, without saying anything else about it.
function Get-KeyShape([string]$Provider, [string]$Key) {
    $Key = ([string]$Key).Trim()
    if ($Provider -eq 'claude') { $prefix = 'sk-ant-'; $company = 'an Anthropic key' } else { $prefix = 'AIza'; $company = 'a Google key' }
    if ($Key.StartsWith($prefix, [StringComparison]::Ordinal)) { return "shaped like $company ($prefix...)" }
    return "NOT shaped like $company ($prefix...), which this provider needs"
}

# Where the report goes: the Desktop, unless the Desktop is synced to OneDrive, where a file is uploaded
# the moment it is written. Then the demo's own reports folder, which stays on this laptop.
function Get-ReportFolder([string]$Desktop) {
    $synced = $false
    if ($Desktop -match '(?i)\\OneDrive( - [^\\]+)?\\') { $synced = $true }
    foreach ($root in @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer)) {
        if ($root -and $Desktop.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $synced = $true }
    }
    if ($synced) { return [pscustomobject]@{ Folder = $script:DemoReportDir; Synced = $true } }
    return [pscustomobject]@{ Folder = $Desktop; Synced = $false }
}

# What a program printed to its standard output (Invoke-Native's Out), when it succeeded; otherwise
# nothing. Never its error output: Windows PowerShell turns each stderr line into an error record, and git's
# and dotnet's error lines carry paths and names (git writes C:/Users/... with forward slashes, which the
# user folder scrub does not match). Whoever calls it says that it failed in a fixed sentence.
function Get-NativeOutput($Result) {
    if (-not $Result -or $Result.ExitCode -ne 0) { return @() }
    return @($Result.Out | Where-Object { $null -ne $_ })
}

# Why a demo window's program stopped, from backend.exited or service.exited. start.ps1 writes "exit code
# <n>" or a fixed sentence, or, when the program could not run at all, "could not run (<Type>): <message>"
# (an older start wrote the message alone). The exit code, the fixed sentence and the type are shown. A
# message is never shown; it is only matched for a Windows block (Blocked), so the top of the report can
# say what to do about one.
function Get-ExitedReason([string]$Path) {
    $reason = ((Read-SharedLog $Path) -join ' ').Trim()
    $blocked = [bool]($reason -match 'Application Control|blocked by|group policy')
    if ($reason -cmatch '^exit code -?[0-9]{1,11}$' -or $reason -ceq 'unknown' -or $reason -ceq 'still running; close this window to end it') {
        $text = $reason
    }
    elseif (-not $reason) {
        $text = 'no reason recorded'
    }
    elseif ($reason -cmatch '^could not run \(([A-Za-z_][A-Za-z0-9_.`+]{0,200})\)(: |$)') {
        $text = 'it could not run (' + $Matches[1] + '; the error message is not shown here)'
    }
    else {
        $text = 'it could not run (start recorded an error message, not shown here)'
    }
    return [pscustomobject]@{ Text = $text; Blocked = $blocked }
}

# One GET whose answer is never read: the status code, or 0 when there was no HTTP answer. Validated by
# Windows like Invoke-Probe.
function Get-StatusOnly([string]$Url) {
    $response = $null
    try {
        $request = [Net.HttpWebRequest]::Create($Url)
        $request.Method = 'GET'
        $request.Timeout = 5000
        $response = $request.GetResponse()
    }
    catch {
        $exception = $_.Exception
        while ($exception -and -not ($exception -is [Net.WebException])) { $exception = $exception.InnerException }
        if ($exception -and $exception.Response) { $response = $exception.Response } else { return 0 }
    }
    try { return [int]$response.StatusCode } finally { $response.Close() }
}

# The last lines of a log, filtered, each indented, after a line saying how many there are. -Raw for a log
# the windows' log writer did not write (see Select-DemoLogLines).
function Add-LogTail([string]$Path, [int]$Count, [switch]$Raw) {
    $lines = @(Select-DemoLogLines -Raw:$Raw @(Read-SharedLog $Path))
    while ($lines.Count -gt 0 -and -not $lines[$lines.Count - 1]) {
        if ($lines.Count -eq 1) { $lines = @() } else { $lines = @($lines[0..($lines.Count - 2)]) }
    }
    $info = New-Object IO.FileInfo($Path)
    Add-Line ('  ' + $info.Name + ': ' + $lines.Count + ' lines, last written ' + (Get-FileTime $Path) + '. The last ' + [Math]::Min($Count, $lines.Count) + ':')
    $start = [Math]::Max(0, $lines.Count - $Count)
    for ($i = $start; $i -lt $lines.Count; $i++) {
        Add-Line ('    ' + $lines[$i])
    }
    return $lines
}

# The ERROR and FATAL lines of a ScreenTail log (both sinks write "HH:mm:ss.fff LEVEL category: ...").
function Get-ErrorLines([string[]]$Lines) {
    return @($Lines | Where-Object { $_ -match '^\d\d:\d\d:\d\d\.\d{3} (ERROR|FATAL) ' })
}

# --- The end-to-end runner's logs -----------------------------------------------------------------------

# What run-e2e.ps1 leaves in its folder ($script:DemoE2eDir on a laptop): e2e.log, its own PASS and FAIL
# lines with the driver's, and what each process it starts printed. Nothing filtered them as they were
# written, and the folder can also hold other files (devcert.cer, briefly, on a CI runner). Only these
# names are read, from that folder only. The driver has no file of its own: the runner writes its lines
# into e2e.log.
$script:E2eRoles = @('backend', 'service', 'ui', 'driver')
$script:E2eLogNames = @('e2e.log') + @($script:E2eRoles | ForEach-Object { "$_.out.log"; "$_.err.log" })

# Loads the log filter for every part of the report that reads a log or a program's output (the demo logs
# section, the end-to-end section, Select-ProgramOutputLines). Returns the filter to dot-source, or $null.
# The guard is the probe: the text has to define ConvertTo-DemoLogLine itself, and once loaded that
# function has to withhold a line the logger did not write, an exception's message, and the reason in a
# line start.ps1 wrote (which can be an exception's message).
#
# The hash is not a guard in the report. The report passes common.ps1's own in-memory source and the hash
# common.ps1 computed from that same text, so they always match; this is not start.ps1's check of a copy
# on disk. It only refuses a text other than the one it is given the hash of (report.Tests.ps1 uses that).
function Get-CheckedLogFilter([string]$Source, [string]$Hash) {
    if (-not $Source -or -not $Hash) { return $null }
    if (-not [string]::Equals((Get-DemoLogWriterHash $Source), $Hash, [StringComparison]::Ordinal)) { return $null }
    try {
        $block = [scriptblock]::Create($Source)
        # It has to define the filter itself, or the probe below would only test the one already loaded.
        $defines = @($block.Ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertTo-DemoLogLine' }, $false))
        if ($defines.Count -eq 0) { return $null }
        . $block
        $state = @{ InLogger = $false; Reading = $true }
        $plain = ConvertTo-DemoLogLine 'not a line the logger wrote' $state
        $null = ConvertTo-DemoLogLine '00:00:00.000 ERROR Probe: probe' $state
        $message = ConvertTo-DemoLogLine '    System.Exception: a message to withhold' $state
        $started = ConvertTo-DemoLogLine '[start.ps1] The backend could not run: a message to withhold' @{ InLogger = $false; Reading = $true }
        $typed = ConvertTo-DemoLogLine '[start.ps1] The backend could not run (System.Exception): a message to withhold' @{ InLogger = $false; Reading = $true }
    }
    catch {
        return $null
    }
    if (-not ([string]$plain -cmatch '^\[line not from ScreenTail''s logger withheld, [0-9]+ chars\]$')) { return $null }
    if (-not [string]::Equals([string]$message, '    System.Exception: [message withheld]', [StringComparison]::Ordinal)) { return $null }
    if ([string]$started -cmatch 'message to withhold') { return $null }
    if ([string]$typed -cmatch 'message to withhold') { return $null }
    return $block
}

# Said after "The log filter could not be checked": when PowerShell is not in FullLanguage mode (an
# Application Control or AppLocker policy puts it in ConstrainedLanguage), the check cannot run at all, and
# that is the likely reason. Nothing otherwise.
function Get-LanguageModeNote([string]$Mode = [string]$ExecutionContext.SessionState.LanguageMode) {
    if (-not $Mode -or $Mode -ceq 'FullLanguage') { return '' }
    $shown = 'a restricted'
    if ($Mode -cmatch '^[A-Za-z]{1,40}$') { $shown = $Mode }
    return " PowerShell runs in $shown language mode here (FullLanguage is needed), which is likely why."
}

# The lines of a crash report (CrashReport.cs) that the report may show: exception types, stack frames,
# "caused by" and blank lines. A type line is a dotted name whose last part starts upper case and ends in
# Exception or Error (so "Payroll.Budget.xlsx" or "jane.doe" is not one); any text after a colon is
# dropped (an older or a planted file could hold a message there). A frame is kept only as at least two
# dotted parts of identifier characters and then a method-like name (upper case or "<" first, or .ctor),
# with an optional line number; any other "at" line becomes "  at [frame withheld]". Every other line (the
# product, the time, the Windows version, anything else) is left out. What was dropped is counted.
$script:CrashTypePattern = '^((?:[A-Za-z_][A-Za-z0-9_`]{0,200}\.){1,40}(?:[A-Za-z_][A-Za-z0-9_`]{0,200}\+){0,10}[A-Z][A-Za-z0-9_`]{0,200}(?:Exception|Error))(\s*:.*)?$'
$script:CrashFramePattern = '^\s+at (?:[A-Za-z_<][A-Za-z0-9_`+<>$|-]{0,200}\.){2,40}(?:[A-Z<][A-Za-z0-9_`+<>$|-]{0,200}|\.c?ctor)(:[0-9]{1,7})?$'
function Select-CrashReportLines([string[]]$Lines) {
    $kept = New-Object System.Collections.Generic.List[string]
    $dropped = 0
    foreach ($line in $Lines) {
        $line = [string]$line
        if ($line.Length -eq 0 -or $line -ceq 'caused by') {
            $kept.Add($line)
        }
        elseif ($line -cmatch $script:CrashFramePattern) {
            $kept.Add($line)
        }
        elseif ($line -cmatch '^\s+at ') {
            $kept.Add('  at [frame withheld]')
            $dropped++
        }
        elseif ($line -cmatch $script:CrashTypePattern) {
            $kept.Add($Matches[1])
            if ($Matches[2]) { $dropped++ }
        }
        else {
            $dropped++
        }
    }
    if ($dropped -gt 0) { Add-Removed 'crash report lines, or text after a type, that are not a type or a frame' $dropped }
    return $kept.ToArray()
}

# At most $Cap bytes of a file, opened for shared reading as Read-SharedLog does: the last ones, or with
# -Head the first. A file with a NUL byte in it is not text and is not decoded. The lines (without the
# one the cut went through), whether it was cut, whether it was binary, whether it could not be read.
function Read-LogBytes([string]$Path, [long]$Cap, [switch]$Head) {
    $result = [pscustomobject]@{ Lines = @(); Cut = $false; Binary = $false; Failed = $false }
    $bytes = $null
    $count = 0
    try {
        $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
        try {
            $length = $stream.Length
            if ($length -gt $Cap) {
                $result.Cut = $true
                if (-not $Head) { [void]$stream.Seek($length - $Cap, [IO.SeekOrigin]::Begin) }
                $length = $Cap
            }
            $bytes = New-Object byte[] ([int]$length)
            while ($count -lt $length) {
                $got = $stream.Read($bytes, $count, ([int]$length - $count))
                if ($got -le 0) { break }
                $count += $got
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        $result.Failed = $true
        return $result
    }
    if ($count -gt 0 -and [Array]::IndexOf($bytes, [byte]0, 0, $count) -ge 0) {
        $result.Binary = $true
        return $result
    }
    $text = (New-Object Text.UTF8Encoding $false).GetString($bytes, 0, $count)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
    $lines = @($text -split "`r?`n")
    if ($result.Cut -and $lines.Count -gt 1) {
        if ($Head) { $lines = @($lines[0..($lines.Count - 2)]) } else { $lines = @($lines[1..($lines.Count - 1)]) }
    }
    $result.Lines = $lines
    return $result
}

# A file of the folder by one of the runner's names, or $null when there is none.
function Get-E2eFile([IO.DirectoryInfo]$Folder, [string]$Name) {
    $info = New-Object IO.FileInfo([IO.Path]::Combine($Folder.FullName, $Name))
    if (-not $info.Exists) { return $null }
    return $info
}

function Test-ReparsePoint([IO.FileSystemInfo]$Item) {
    return (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

# When the run started, from e2e.log's first line ("ScreenTail end-to-end run (ST-013), <time> on <name>"),
# as a time and as text. Only the digits are taken from the line.
function Get-E2eRunStart([string[]]$Lines) {
    if ($Lines.Count -eq 0) { return $null }
    $pattern = '^ScreenTail end-to-end run \(ST-013\), ([0-9]{4})-([0-9]{2})-([0-9]{2}) ([0-9]{2})[:.]([0-9]{2})[:.]([0-9]{2}) on '
    if (-not ([string]$Lines[0] -cmatch $pattern)) { return $null }
    $found = $Matches
    try {
        $parts = @(1..6 | ForEach-Object { [int]$found[$_] })
        $time = New-Object DateTime($parts[0], $parts[1], $parts[2], $parts[3], $parts[4], $parts[5], ([DateTimeKind]::Local))
    }
    catch {
        return $null
    }
    return [pscustomobject]@{ Time = $time; Text = $time.ToString('yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture) }
}

# The result of a run from e2e.log's lines. Nothing of a line is copied: each fact is put together again
# from what an anchored pattern allows (PASS or FAIL, a step's id and name, counts), so what a step printed
# after its name (an exception's message, a tool's output, a path, a window's title) never reaches the
# report.
function Get-E2eRunFacts([string[]]$Lines) {
    $facts = [pscustomobject]@{ Summary = $null; Driver = $null; Failed = (New-Object System.Collections.Generic.List[string]) }
    foreach ($line in $Lines) {
        $line = [string]$line
        if ($line -cmatch '^PASS e2e: all ([0-9]{1,5}) steps passed\.') {
            $facts.Summary = "PASS e2e: all $([int]$Matches[1]) steps passed."
        }
        elseif ($line -cmatch '^FAIL e2e: ([0-9]{1,5}) step\(s\) failed, ([0-9]{1,5}) passed\.') {
            $facts.Summary = "FAIL e2e: $([int]$Matches[1]) step(s) failed, $([int]$Matches[2]) passed."
        }
        elseif ($line -cmatch '^PASS loop: all ([0-9]{1,5}) steps passed\.$') {
            $facts.Driver = "PASS loop: all $([int]$Matches[1]) steps passed."
        }
        elseif ($line -cmatch '^FAIL loop: ([0-9]{1,5}) step\(s\) failed, ([0-9]{1,5}) passed\.$') {
            $facts.Driver = "FAIL loop: $([int]$Matches[1]) step(s) failed, $([int]$Matches[2]) passed."
        }
        elseif ($line -cmatch '^FAIL loop: interrupted') {
            $facts.Driver = 'FAIL loop: interrupted.'
        }
        elseif ($line -cmatch '^FAIL ((?:setup|loop|teardown)-[0-9]{2,3}) ([a-z][a-z0-9-]{0,40}) \(') {
            $facts.Failed.Add("FAIL $($Matches[1]) $($Matches[2])")
        }
        elseif ($line -cmatch '^FAIL e2e-runner: ') {
            $facts.Failed.Add('FAIL e2e-runner (the runner itself stopped)')
        }
    }
    return $facts
}

# The last lines of one of a process's logs, through the same filter and scrub as the demo logs. $RunStart
# says whether the file is older than the run (the run stopped before it started that process).
function Add-E2eLogTail([IO.FileInfo]$File, [int]$Count, $RunStart) {
    $name = $File.Name
    $when = 'last written ' + $File.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
    if ($RunStart -and $File.LastWriteTime -lt $RunStart) { $when += ', before the last run started (from an earlier run)' }
    if (Test-ReparsePoint $File) {
        Add-Line ('  ' + $name + ': a link to another place, so not read.')
        return
    }
    $read = Read-LogBytes -Path $File.FullName -Cap $script:DemoLogCapBytes
    if ($read.Failed) {
        Add-Line ('  ' + $name + ': could not be read (' + $when + ').')
        return
    }
    if ($read.Binary) {
        Add-Removed 'end-to-end files that are not text (not read)'
        Add-Line ('  ' + $name + ': not text, so not read (' + $when + ').')
        return
    }
    $lines = @(Select-DemoLogLines -Raw @($read.Lines))
    while ($lines.Count -gt 0 -and -not $lines[$lines.Count - 1]) {
        if ($lines.Count -eq 1) { $lines = @() } else { $lines = @($lines[0..($lines.Count - 2)]) }
    }
    if ($lines.Count -eq 0) {
        Add-Line ('  ' + $name + ': empty, ' + $when + '.')
        return
    }
    $size = ''
    if ($read.Cut) { $size = ' in its last ' + [int]($script:DemoLogCapBytes / 1MB) + ' MB' }
    Add-Line ('  ' + $name + ': ' + $lines.Count + ' lines' + $size + ', ' + $when + '. The last ' + [Math]::Min($Count, $lines.Count) + ':')
    $start = [Math]::Max(0, $lines.Count - $Count)
    for ($i = $start; $i -lt $lines.Count; $i++) {
        Add-Line ('    ' + $lines[$i])
    }
}

# The "End-to-end run" section, only when the runner's folder exists. Every line of it goes through
# Add-Line, so through the final scrub; the processes' logs go through the checked filter first. If the
# filter cannot be checked, the section says so and shows nothing from the run.
function Add-E2eReport {
    param([string]$Folder, [string]$FilterSource, [string]$FilterHash, [int]$Tail = $script:LogTail)

    if (-not $Folder) { return }
    try {
        $dir = New-Object IO.DirectoryInfo($Folder)
        if (-not $dir.Exists) { return }
    }
    catch {
        return
    }

    Invoke-Section 'End-to-end run (the last scripts\windows\e2e\run-e2e.cmd run on this laptop)' {
        # A fixed sentence on any failure: an exception's message can carry a path or a name. (Invoke-Section
        # would also give only a fixed sentence and the type; this one says the rest of the section is left out.)
        try {
            Add-Fact 'Folder' $dir.FullName
            if (Test-ReparsePoint $dir) {
                Add-Line '  The folder is a link to another place, so nothing in it was read.'
                return
            }
            $checked = Get-CheckedLogFilter -Source $FilterSource -Hash $FilterHash
            if (-not $checked) {
                Add-Line ('  The log filter could not be checked, so nothing from the end-to-end run is shown.' + (Get-LanguageModeNote))
                return
            }
            # The checked filter, loaded into this section's own scope, is the ConvertTo-DemoLogLine that
            # Select-DemoLogLines finds when it is called from here.
            . $checked

            $runStart = $null
            $logFile = Get-E2eFile $dir 'e2e.log'
            if (-not $logFile) {
                Add-Fact 'e2e.log' 'not there (the last run stopped before writing it)'
            }
            elseif (Test-ReparsePoint $logFile) {
                Add-Fact 'e2e.log' 'a link to another place, so not read'
            }
            else {
                $written = $logFile.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
                $head = Read-LogBytes -Path $logFile.FullName -Cap 4096 -Head
                $all = Read-LogBytes -Path $logFile.FullName -Cap $script:DemoLogCapBytes
                if ($head.Binary -or $all.Binary) {
                    Add-Removed 'end-to-end files that are not text (not read)'
                    Add-Fact 'e2e.log' 'not text, so not read'
                }
                elseif ($head.Failed -or $all.Failed) {
                    Add-Fact 'e2e.log' 'could not be read'
                }
                else {
                    $start = Get-E2eRunStart @($head.Lines)
                    if ($start) {
                        $runStart = $start.Time
                        $took = [int][Math]::Round(($logFile.LastWriteTime - $start.Time).TotalMinutes)
                        Add-Fact 'Run started' ($start.Text + ' (e2e.log last written ' + $written + ', about ' + [Math]::Max(0, $took) + ' min later)')
                    }
                    else {
                        Add-Fact 'Run started' ('unknown: e2e.log has no start line (last written ' + $written + ')')
                    }
                    $facts = Get-E2eRunFacts @($all.Lines)
                    if ($facts.Summary) {
                        Add-Fact 'Result' $facts.Summary
                    }
                    else {
                        Add-Fact 'Result' 'none: the run was cut off, or is still running'
                    }
                    if ($facts.Driver) { Add-Fact 'The driver''s result' $facts.Driver }
                    if ($facts.Failed.Count -eq 0) {
                        Add-Fact 'Steps that failed' 'none'
                    }
                    else {
                        Add-Fact 'Steps that failed (id and status only)' "$($facts.Failed.Count):"
                        foreach ($step in @($facts.Failed | Select-Object -First 50)) { Add-Line ('    ' + $step) }
                        if ($facts.Failed.Count -gt 50) { Add-Line "    ... and $($facts.Failed.Count - 50) more" }
                    }
                    if ($facts.Summary -and $facts.Summary.StartsWith('FAIL', [StringComparison]::Ordinal)) {
                        $when = 'at a time not recorded'
                        if ($start) { $when = 'started ' + $start.Text }
                        $first = 'see its section'
                        if ($facts.Failed.Count -gt 0) { $first = 'first failed step: ' + $facts.Failed[0] }
                        Add-Notice ("The last end-to-end run ($when) failed ($first). The 'End-to-end run' section at the end has its logs.")
                    }
                }
            }
            $others = @($dir.GetFiles() | Where-Object { $script:E2eLogNames -notcontains $_.Name })
            Add-Fact 'Other files in the folder (not read)' $others.Count

            foreach ($role in $script:E2eRoles) {
                Add-Line ''
                Add-Line ("  -- $role (the last $Tail lines of each log; only the logger's lines are kept)")
                $files = @(@("$role.out.log", "$role.err.log") | ForEach-Object { Get-E2eFile $dir $_ } | Where-Object { $_ })
                if ($files.Count -eq 0) {
                    if ($role -eq 'driver') {
                        Add-Line '  No log file of its own: the runner writes the driver''s lines into e2e.log, summed up above.'
                    }
                    else {
                        Add-Line "  No log file: the last run stopped before it started the $role."
                    }
                    continue
                }
                foreach ($file in $files) {
                    try {
                        Add-E2eLogTail -File $file -Count $Tail -RunStart $runStart
                    }
                    catch {
                        Add-Line ('  ' + $file.Name + ': could not be read.')
                    }
                }
            }
        }
        catch {
            Add-Line '  Part of the end-to-end run could not be read, so the rest of this section is left out.'
        }
    }
}

# The capture service's own --capabilities check, run once, with its answer in the report. When it cannot
# be started, a fixed sentence and the exception's type; the message is only matched for a Windows block.
function Add-CapabilityCheck([string]$Exe, [string]$WorkingDirectory, [int]$Seconds = 60) {
    $run = $null
    try {
        $run = Invoke-WithTimeout -FilePath $Exe -Arguments '--capabilities' -WorkingDirectory $WorkingDirectory -Seconds $Seconds
    }
    catch {
        $caught = $_
        Add-Fact 'Capability check' ('could not run (' + (Get-ExceptionTypeName $caught) + ')')
        if ([string]$caught.Exception.Message -match 'Application Control|blocked by|group policy') {
            Add-Problem 12 'Windows blocks the capture service from running (an Application Control policy). Fresh unsigned builds trip it on this laptop.' 'Allow it in Windows Security > App & browser control, or ask the developer (ST-112 is the signing fix).'
        }
    }
    if ($run -and $run.TimedOut) {
        Add-Fact 'Capability check' "did not finish within $Seconds seconds"
    }
    elseif ($run) {
        Add-CapabilityReport -Out $run.Out -Err $run.Err -ExitCode $run.ExitCode
    }
}

# One check of the capability report, as the report may show it. When a probe throws, the service puts
# the exception's message into the check (state "unknown", the message in fix_hint), so of a check in any
# state but ok, degraded or blocked only a fixed sentence is shown, never its message or fix_hint. A
# capability's name is shown only when it is one of the service's snake_case names.
function Get-CapabilityCheckView($Check) {
    $name = [string]$Check.capability
    if (-not ($name -cmatch '^[a-z][a-z0-9_]{0,40}$')) { $name = 'a capability' }
    $state = [string]$Check.state
    if ($state -cin @('ok', 'degraded', 'blocked')) {
        $message = [string]$Check.message
        $hint = [string]$Check.fix_hint
    }
    else {
        $state = 'unknown'
        $message = 'ScreenTail couldn''t check this (reason withheld).'
        $hint = ''
    }
    $line = "$state. $message"
    if ($hint) { $line += " Fix: $hint" }
    return [pscustomobject]@{ Name = $name; State = $state; Message = $message; Hint = $hint; Line = $line }
}

# What the --capabilities check printed, in the report: its JSON report, or when there is none, only what
# the service's logger or its fixed sentences said (a crash's message is withheld).
function Add-CapabilityReport([string]$Out, [string]$Err, [int]$ExitCode) {
    $text = [string]$Out
    $at = $text.IndexOf('{')
    $report = $null
    if ($at -ge 0) {
        try { $report = $text.Substring($at) | ConvertFrom-Json } catch { $report = $null }
    }
    if (-not $report) {
        Add-Fact 'Capability check' "exit code $ExitCode, no report"
        $said = @(Select-ProgramOutputLines ([string]$Err + "`n" + $text))
        foreach ($line in @($said | Select-Object -First 10)) { Add-Line ('    ' + $line) }
        return
    }
    Add-Fact 'Can capture' (Get-YesNo $report.can_capture)
    $views = @(@($report.checks) | Where-Object { $_ } | ForEach-Object { Get-CapabilityCheckView $_ })
    foreach ($view in $views) { Add-Fact $view.Name $view.Line }
    if (-not $report.can_capture) {
        $first = @($views | Where-Object { $_.State -eq 'blocked' -or $_.State -eq 'unknown' }) | Select-Object -First 1
        $fix = 'See "What Windows lets the capture service do" below.'
        if ($first -and $first.Hint) { $fix = $first.Hint }
        $what = 'one of its checks failed'
        if ($first) { $what = "$($first.Name): $($first.Message)" }
        Add-Problem 85 "Windows is not letting ScreenTail capture ($what)." $fix
    }
    else {
        foreach ($view in @($views | Where-Object { $_.State -ne 'ok' -and $_.Name -ne 'elevated_windows' })) {
            $hint = ''
            if ($view.Hint) { $hint = " Fix: $($view.Hint)" }
            Add-Notice "$($view.Name) is $($view.State): $($view.Message)$hint"
        }
    }
    $said = @(Select-ProgramOutputLines ([string]$Err))
    foreach ($line in @($said | Select-Object -First 5)) { Add-Line ('    stderr: ' + $line) }
}

# The "Logs" section: the last lines of the demo windows' logs, of the run before, of a backend log from an
# older start, and the newest crash report. Every log goes through the checked filter (Get-CheckedLogFilter,
# as the end-to-end section does); if it cannot be checked, the section says so and shows no log.
function Add-DemoLogsReport {
    param([object[]]$Logs, [string]$OldBackendLog, [string]$CrashDir, [string]$FilterSource, [string]$FilterHash, [int]$Tail = $script:LogTail)

    Invoke-Section ("Logs (the last $Tail lines of each; only the logger's lines are kept)") {
        $checked = Get-CheckedLogFilter -Source $FilterSource -Hash $FilterHash
        if (-not $checked) {
            Add-Line ('  The log filter could not be checked, so no log is shown.' + (Get-LanguageModeNote))
            return
        }
        # Loaded into this section's own scope, where Select-DemoLogLines finds it.
        . $checked

        foreach ($log in $Logs) {
            Add-Line ''
            Add-Line ("  -- $($log.Name)")
            $lines = @()
            if (Test-Path -LiteralPath $log.Path) {
                $lines = @(Add-LogTail -Path $log.Path -Count $Tail)
            }
            else {
                Add-Line ('  No log yet at ' + $log.Path + '. start.cmd writes it from now on; if the demo was started before this')
                Add-Line '  change, run stop.cmd and start.cmd, try again, then run report.cmd.'
            }
            $older = Get-OlderLogPath $log.Path
            if (Test-Path -LiteralPath $older) {
                Add-Line '  The run before it (or the start of a long run):'
                $null = Add-LogTail -Path $older -Count 100
            }
            $errors = @(Get-ErrorLines $lines)
            if ($errors.Count -gt 0) {
                Add-Problem $log.Rank ("The $($log.Name.ToLower()) logged $($errors.Count) error(s). The first: " + $errors[0]) 'Send this report to the developer; the log below has the details.'
            }
            if ($log.Name -eq 'Backend' -and @($lines | Where-Object { $_ -match '\b429\b|RESOURCE_EXHAUSTED|quota' }).Count -gt 0) {
                if ($script:Provider -eq 'claude') {
                    Add-Notice 'The backend log mentions a quota or HTTP 429: Anthropic is rate limiting the key. Wait a minute; a waiting session drafts by itself.'
                }
                else {
                    Add-Notice 'The backend log mentions a quota or HTTP 429: the free Gemini key allows about 20 drafts a day and 5 a minute. Wait a minute, or until tomorrow.'
                }
            }
        }

        if ($OldBackendLog -and (Test-Path -LiteralPath $OldBackendLog)) {
            Add-Line ''
            Add-Line '  -- Backend, from an older start.cmd (before the logs folder)'
            # An older start wrote this file itself, without the log writer: read as raw output.
            $null = Add-LogTail -Path $OldBackendLog -Count $Tail -Raw
        }

        Add-Line ''
        Add-Line '  -- UI'
        Add-Line '  The UI writes no log file: it has no console window, and its only record is a crash report,'
        Add-Line '  which is written only when SCREENTAIL_CRASH_REPORTS=1 is set.'
        $crashes = @()
        if ($CrashDir -and (Test-Path -LiteralPath $CrashDir)) {
            $crashes = @(Get-ChildItem -LiteralPath $CrashDir -Filter '*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        }
        Add-Fact 'Crash reports (service and UI)' $crashes.Count
        if ($crashes.Count -gt 0) {
            Add-Line ('  The newest, ' + $crashes[0].Name + ' (exception types and stack frames only):')
            foreach ($line in @(@(Select-CrashReportLines @(Read-SharedLog $crashes[0].FullName)) | Select-Object -First 60)) { Add-Line ('    ' + $line) }
        }
    }
}

# Whether a program is on PATH. Its own function so that report.Tests.ps1 can stand in for it.
function Test-Program([string]$Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

# The "The .NET SDK" section. Of dotnet only what it printed when it succeeded (Get-NativeOutput).
function Add-DotNetReport([string]$Repo) {
    Invoke-Section 'The .NET SDK' {
        if (-not (Test-Program 'dotnet')) {
            Add-Fact '.NET SDK' 'dotnet is not installed, or not on PATH'
            Add-Problem 10 'dotnet is not installed, or not on PATH, so nothing of the demo can be built.' 'Install the .NET 10 SDK (winget install --id Microsoft.DotNet.SDK.10 -e --source winget), then open a new PowerShell window.'
            $script:HaveDotnet = $false
            return
        }
        $script:HaveDotnet = $true
        # Of each program run in this file, only its output when it succeeded (Get-NativeOutput); its error
        # output is never shown, and a failure is said in a fixed sentence.
        $version = Invoke-Native -FilePath 'dotnet' -ArgumentList @('--version') -WorkingDirectory $Repo
        $chosen = (@(Get-NativeOutput $version) | Select-Object -Last 1)
        if ($chosen -match '^10\.') {
            Add-Fact '.NET SDK this clone uses' $chosen
        }
        else {
            Add-Fact '.NET SDK this clone uses' 'none that global.json accepts'
            Add-Problem 10 'No .NET 10 SDK that this repository accepts is installed.' 'Install it (winget install --id Microsoft.DotNet.SDK.10 -e --source winget), then open a new PowerShell window.'
        }
        $sdks = Invoke-Native -FilePath 'dotnet' -ArgumentList @('--list-sdks')
        if ($sdks.ExitCode -eq 0) {
            $installed = @(@(Get-NativeOutput $sdks) | ForEach-Object { ($_ -split ' ')[0] } | Where-Object { $_ })
            Add-Fact '.NET SDKs installed' ($installed -join ', ')
        }
        else {
            Add-Fact '.NET SDKs installed' "unknown: dotnet --list-sdks failed (exit code $($sdks.ExitCode))"
        }
    }
}

# The "The clone" section. Of git only what it printed when it succeeded (Get-NativeOutput).
function Add-CloneReport([string]$Repo) {
    Invoke-Section 'The clone' {
        Add-Fact 'Folder' $Repo
        if ($Repo -match '(?i)actions-runner|\\_work\\') {
            Add-Notice 'The demo is running from the CI runner''s folder. Use your own clone (for example C:\src\ScreenTail): the runner changes its folder under you.'
        }
        if (-not (Test-Program 'git')) {
            Add-Fact 'git' 'not installed or not on PATH, so the commit is unknown'
            return
        }
        # Of git, only what it printed when it succeeded (Get-NativeOutput). Its error output (such as "unsafe
        # repository" with the folder in it) is never shown, only that it failed.
        $branch = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $Repo, 'rev-parse', '--abbrev-ref', 'HEAD')
        $name = @(Get-NativeOutput $branch) | Select-Object -First 1
        if ($name) { Add-Fact 'Branch' $name } else { Add-Fact 'Branch' "unknown: git could not read the clone (exit code $($branch.ExitCode))" }
        # The commit's subject only when origin has the commit, so it is already public. A commit made only on
        # this laptop is shown by its id and date.
        $published = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $Repo, 'branch', '-r', '--contains', 'HEAD')
        $onOrigin = (@(Get-NativeOutput $published | Where-Object { $_ -match '^\s*origin/' }).Count -gt 0)
        if ($onOrigin) { $format = '--format=%H %cd %s' } else { $format = '--format=%H %cd (not on origin; subject not shown)' }
        $head = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $Repo, 'log', '-1', $format, '--date=short')
        $commit = @(Get-NativeOutput $head) | Select-Object -First 1
        if ($commit) { Add-Fact 'Commit' $commit } else { Add-Fact 'Commit' "unknown: git could not read the commit (exit code $($head.ExitCode))" }

        $compare = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $Repo, 'rev-list', '--left-right', '--count', 'origin/main...HEAD')
        $counts = @(([string](@(Get-NativeOutput $compare) | Select-Object -First 1)).Trim() -split '\s+')
        if ($counts.Count -ge 2 -and $counts[0] -cmatch '^[0-9]+$' -and $counts[1] -cmatch '^[0-9]+$') {
            Add-Fact 'Against origin/main (last fetch)' ("$($counts[0]) behind, $($counts[1]) ahead")
            if ([int]$counts[0] -gt 0) {
                Add-Notice "The clone is $($counts[0]) commit(s) behind origin/main as of its last fetch. A 'git pull' may already fix what went wrong."
            }
        }

        # Changed files the repository tracks, by name. Untracked files are only counted: their names are the
        # owner's, not the project's. --no-optional-locks: status does not even refresh git's index file.
        $status = Invoke-Native -FilePath 'git' -ArgumentList @('--no-optional-locks', '-C', $Repo, 'status', '--porcelain', '-uno')
        $changed = @(Get-NativeOutput $status | Where-Object { $_.Trim() })
        if ($status.ExitCode -ne 0) {
            Add-Fact 'Changed tracked files' "unknown: git status failed (exit code $($status.ExitCode))"
        }
        elseif ($changed.Count -eq 0) {
            Add-Fact 'Changed tracked files' 'none'
        }
        else {
            Add-Fact 'Changed tracked files' "$($changed.Count):"
            foreach ($line in ($changed | Select-Object -First 20)) { Add-Line ('    ' + $line) }
            if ($changed.Count -gt 20) { Add-Line "    ... and $($changed.Count - 20) more" }
            Add-Notice "The clone has $($changed.Count) changed file(s) (listed under 'The clone'). The developer may ask you to undo them."
        }
        $untracked = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $Repo, 'ls-files', '--others', '--exclude-standard')
        if ($untracked.ExitCode -eq 0) {
            Add-Fact 'Untracked files (count only)' (@(Get-NativeOutput $untracked | Where-Object { $_.Trim() }).Count)
        }
        else {
            Add-Fact 'Untracked files (count only)' "unknown: git ls-files failed (exit code $($untracked.ExitCode))"
        }
    }
}

# For report.Tests.ps1: everything above is defined, nothing below runs.
if ($FunctionsOnly) { return }

# --- Collecting -----------------------------------------------------------------------------------------

Write-Host 'Writing the ScreenTail demo report. This takes up to a minute and changes nothing.'
$now = Get-Date

# The secrets are read first so that the scrub knows their exact values. They are never written.
try {
    $script:SecretsRead = Read-SecretsQuietly
    $script:Secrets = $script:SecretsRead.Table
    $known = @()
    foreach ($name in $script:Secrets.Keys) {
        $value = [string]$script:Secrets[$name]
        if ($name -match '(?i)key|secret|password|token|connection|credential') {
            $known += $value
            $known += $value.Trim()
        }
    }
    $connection = $script:Secrets[$script:KeyDatabase]
    if ($connection) {
        try {
            $script:Database = Get-ConnectionParts $connection
            $known += $script:Database.Password
        }
        catch {
            $script:Database = $null
        }
    }
    foreach ($scope in @('User', 'Machine')) {
        $known += [string][Environment]::GetEnvironmentVariable('SCREENTAIL_DEVICE_TOKEN', $scope)
    }
    # Only values of 12 characters or more are looked for by value: keys, tokens and connection strings
    # all are. A short database password (often just "postgres") is left to the name=value rules, because
    # replacing every "postgres" in the report would itself tell the reader the password. Longest first,
    # so a secret that contains another is removed whole.
    $script:KnownSecrets = @($known | Where-Object { $_ -and $_.Length -ge 12 } | Select-Object -Unique | Sort-Object -Property Length -Descending)
}
catch {
    $script:SecretsRead = $null
}

Invoke-Section 'This machine' {
    $os = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $build = [int]$os.CurrentBuild
    $product = [string]$os.ProductName
    if ($build -ge 22000) { $product = $product -replace 'Windows 10', 'Windows 11' }
    $release = $os.DisplayVersion
    if (-not $release) { $release = $os.ReleaseId }
    Add-Fact 'Windows' ("$product $release (build $build.$($os.UBR))")
    Add-Fact 'Processor architecture' $env:PROCESSOR_ARCHITECTURE
    Add-Fact 'PowerShell' $PSVersionTable.PSVersion.ToString()
    Add-Fact 'Display language / formats' ((Get-UICulture).Name + ' / ' + (Get-Culture).Name)
    if ($env:SESSIONNAME -like 'RDP-*') { $session = 'through Remote Desktop' } else { $session = 'at the laptop itself' }
    Add-Fact 'Signed in' $session

    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    Add-Fact 'This report ran as administrator' (Get-YesNo $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))

    $smart = 'not present on this Windows'
    $ci = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue
    if ($ci -and $null -ne $ci.VerifiedAndReputablePolicyState) {
        switch ([int]$ci.VerifiedAndReputablePolicyState) {
            0 { $smart = 'off' }
            1 { $smart = 'on (it can block fresh unsigned builds)' }
            2 { $smart = 'evaluation (it may turn itself on)' }
            default { $smart = "state $($ci.VerifiedAndReputablePolicyState)" }
        }
    }
    Add-Fact 'Smart App Control' $smart
}

Add-DotNetReport -Repo $script:DemoRepo

Add-CloneReport -Repo $script:DemoRepo

Invoke-Section 'Demo components' {
    $state = @(Get-DemoState)
    $parsed = Read-JsonFile $script:DemoStateFile
    if ($parsed -and $parsed -ne 'unreadable' -and $parsed.Written) {
        Add-Fact 'Last recorded by start.cmd' ([string]$parsed.Written)
    }
    else {
        Add-Fact 'Last recorded by start.cmd' 'nothing (start has not run, or stop.cmd ran after it)'
    }

    $labels = [ordered]@{
        'backend-window' = 'Backend window'
        'backend' = 'Backend process'
        'service-window' = 'Capture service window'
        'service' = 'Capture service'
        'ui' = 'UI (tray icon)'
        'database' = 'Database container start opened'
    }
    foreach ($role in $labels.Keys) {
        $entry = @($state | Where-Object { $_.Role -eq $role }) | Select-Object -First 1
        if (-not $entry) {
            if ($role -ne 'database') { Add-Fact $labels[$role] 'not started by start.cmd' }
            $script:States[$role] = 'absent'
            continue
        }
        if ($entry.Kind -eq 'container') {
            Add-Fact $labels[$role] $script:DemoDbContainer
            $script:States[$role] = 'container'
            continue
        }
        $process = Get-EntryProcess $entry
        if ($process) {
            $since = $process.StartTime.ToString('HH:mm')
            Add-Fact $labels[$role] "running (pid $($process.Id), since $since)"
            $script:States[$role] = 'running'
        }
        else {
            Add-Fact $labels[$role] 'NOT running (start.cmd started it; it has stopped since)'
            $script:States[$role] = 'stopped'
        }
    }

    foreach ($name in @('ScreenTail.Service', 'ScreenTail.UI')) {
        $all = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        $mine = @($all | Where-Object { $_.Path -and $_.Path.StartsWith($script:DemoAppDir, [StringComparison]::OrdinalIgnoreCase) })
        $other = $all.Count - $mine.Count
        Add-Fact "$name processes" "$($all.Count) running ($($mine.Count) from the demo's folder, $other from elsewhere)"
        if ($other -gt 0) {
            Add-Notice "A $name that start.cmd did not start is running (an installed ScreenTail, or an earlier run). It can get in the demo's way: quit it from its tray icon."
        }
    }

    foreach ($file in @($script:DemoBackendExited, $script:DemoServiceExited)) {
        if (Test-Path -LiteralPath $file) {
            $reason = Get-ExitedReason $file
            Add-Fact ([IO.Path]::GetFileName($file)) ("$($reason.Text) (written $(Get-FileTime $file))")
            if ($reason.Blocked) {
                Add-Problem 12 'Windows blocked a demo program from running (an Application Control policy). Fresh unsigned builds trip it on this laptop.' 'Allow it in Windows Security > App & browser control, or ask the developer (ST-112 is the signing fix).'
            }
        }
    }

    $live = @($script:States.Keys | Where-Object { $script:States[$_] -eq 'running' })
    $recorded = @($script:States.Keys | Where-Object { $script:States[$_] -eq 'running' -or $script:States[$_] -eq 'stopped' })
    if ($recorded.Count -eq 0) {
        Add-Problem 50 'The demo is not running: there is no record of start.cmd having started it (or stop.cmd has run since).' 'Run scripts\windows\demo\start.cmd, try again, and run report.cmd while it is still running.'
    }
    elseif ($live.Count -eq 0) {
        Add-Problem 50 'Nothing that start.cmd started is still running.' 'Run stop.cmd, then start.cmd, try again, and run report.cmd while the demo is still running.'
    }
    else {
        if ($script:States['backend'] -eq 'stopped' -or $script:States['backend-window'] -eq 'stopped') {
            Add-Problem 55 'The backend has stopped (its window was closed, or it crashed). Without it nothing is drafted.' 'Run stop.cmd, then start.cmd. If it stops again, the backend log at the end of this report says why.'
        }
        if ($script:States['service'] -eq 'stopped' -or $script:States['service-window'] -eq 'stopped') {
            Add-Problem 65 'The capture service has stopped, so nothing is being recorded.' 'Run stop.cmd, then start.cmd. If it stops again, the capture service log at the end of this report says why.'
        }
        if ($script:States['ui'] -eq 'stopped') {
            Add-Problem 70 'The UI (the tray icon) has closed.' 'Run stop.cmd, then start.cmd.'
        }
    }

    $owner = Get-PortOwner $script:DemoBackendPort
    if ($owner) { Add-Fact "Port $script:DemoBackendPort is held by" "$($owner.Name) (pid $($owner.Id))" } else { Add-Fact "Port $script:DemoBackendPort is held by" 'nothing' }

    $health = Invoke-Probe -Url "$script:DemoBackendUrl/health" -TimeoutSeconds 5
    $script:HealthStatus = $health.Status
    if ($health.Status -eq 200) {
        $body = $health.Body
        if ($body.Length -gt 120) { $body = $body.Substring(0, 120) }
        Add-Fact "$script:DemoBackendUrl/health" "HTTP 200 over a trusted certificate: $body"
    }
    elseif ($health.Status -gt 0) {
        Add-Fact "$script:DemoBackendUrl/health" "HTTP $($health.Status)"
        Add-Problem 60 "The backend answers /health with HTTP $($health.Status) instead of 200." 'Run stop.cmd, then start.cmd. If it repeats, send this report to the developer as it is.'
    }
    else {
        $why = $health.Failure
        switch ($health.Failure) {
            'ConnectFailure' { $why = 'nothing answers (the backend is not running)' }
            'TrustFailure' { $why = 'it answers, but Windows does not trust its certificate' }
            'SecureChannelFailure' { $why = 'it answers, but the HTTPS handshake fails' }
            'Timeout' { $why = 'no answer within 5 seconds' }
        }
        Add-Fact "$script:DemoBackendUrl/health" "no HTTP answer: $why"
        if ($health.Failure -eq 'TrustFailure') {
            Add-Problem 30 'The backend answers, but Windows does not trust its certificate, so the capture service refuses it too.' 'Run setup.cmd again and answer Yes when Windows asks about the certificate, then stop.cmd and start.cmd.'
        }
        elseif ($recorded.Count -gt 0 -and $script:States['backend'] -ne 'stopped') {
            Add-Problem 60 "The backend does not answer /health ($why)." 'Run stop.cmd, then start.cmd. The backend log at the end of this report may say why.'
        }
    }

    if ($health.Status -eq 200) {
        # Mapped only while Providers:Demo is on, so its status says whether the running backend has it.
        # Only the status is read. The page lists the demo tickets and any note published to them.
        $demoStatus = Get-StatusOnly "$script:DemoBackendUrl/dev/demo-psa"
        switch ($demoStatus) {
            200 { $script:DemoLive = 'on'; Add-Fact 'Demo PSA in the running backend' 'on (Providers:Demo)' }
            404 { $script:DemoLive = 'off'; Add-Fact 'Demo PSA in the running backend' 'off (Publish has nowhere to go)' }
            default { Add-Fact 'Demo PSA in the running backend' "unclear (HTTP $demoStatus)" }
        }
    }

    if ($script:HaveDotnet) {
        $trusted = Test-DevCertificateTrusted
        Add-Fact 'Development certificate trusted' (Get-YesNo $trusted)
        if (-not $trusted) {
            Add-Problem 30 'The development certificate is missing, expired or not trusted, so the capture service refuses the backend.' 'Run setup.cmd again and answer Yes when Windows asks about the certificate.'
        }
    }
}

Invoke-Section 'PostgreSQL' {
    if ($script:Database) {
        $reachable = Test-TcpPort -HostName $script:Database.Host -Port $script:Database.Port
        Add-Fact 'PostgreSQL answers' ((Get-YesNo $reachable) + " (on port $($script:Database.Port))")
        if (-not $reachable) {
            Add-Problem 40 'PostgreSQL does not answer, so the backend cannot start or keep anything.' 'Start it: the Services app (PostgreSQL entry, Start) or Docker Desktop. Then stop.cmd and start.cmd.'
        }
    }
    else {
        Add-Fact 'PostgreSQL answers' 'unknown: no database connection is saved'
    }
    $services = @(Get-PostgresServices)
    if ($services.Count -gt 0) {
        Add-Fact 'PostgreSQL Windows services' (($services | ForEach-Object { "$($_.Name) ($($_.Status))" }) -join ', ')
    }
    else {
        Add-Fact 'PostgreSQL Windows services' 'none'
    }
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        $docker = Get-DockerState
        $line = $docker
        if ($docker -eq 'running') {
            $container = Get-DbContainerStatus
            if ($container) { $line = "running; $script:DemoDbContainer is: $container" } else { $line = "running; no $script:DemoDbContainer container" }
        }
        Add-Fact 'Docker' $line
    }
    else {
        Add-Fact 'Docker' 'not installed'
    }
}

Invoke-Section 'The CI runner (it can take over the screen)' {
    switch (Get-RunnerState) {
        'job' {
            Add-Fact 'GitHub Actions runner' 'running a job right now'
            Add-Problem 45 'A GitHub Actions job is running on this laptop. Hardware jobs take over the keyboard, mouse and screen, and start and stop ScreenTail themselves.' 'Wait for it to finish (github.com/hiratinspace/ScreenTail/actions), then stop.cmd and start.cmd.'
        }
        'idle' {
            Add-Fact 'GitHub Actions runner' 'on, waiting for a job'
            Add-Notice 'The GitHub Actions runner is on. A hardware job that starts during the demo takes over the screen: close its "run.cmd" window before the demo.'
        }
        default { Add-Fact 'GitHub Actions runner' 'not running' }
    }
}

Invoke-Section 'Settings the demo depends on (yes/no or names only)' {
    if (-not $script:SecretsRead) {
        Add-Fact 'User secrets' 'could not be read'
        return
    }
    if ($script:SecretsRead.Problem) {
        Add-Fact 'User secrets file' $script:SecretsRead.Problem
    }
    else {
        Add-Fact 'User secrets file' "found, written $(Get-FileTime $script:SecretsRead.Path)"
    }
    $table = $script:Secrets

    $keyName = $script:KeyDrafting
    $provider = Get-DraftingProvider $table
    $script:Provider = $provider
    $saved = [string]$table[$script:KeyProvider]
    if ($saved) { $how = "saved as '$saved'" } else { $how = 'not saved, so the default' }
    Add-Fact 'Drafting provider' ((Get-ProviderLabel $provider) + " ($how)")

    # Who the last start told the backend to draft with (start.ps1 writes one word to drafting.txt). Only
    # a known word is shown; anything else is reported as unrecognised, never echoed.
    if (Test-Path -LiteralPath $script:DemoDraftingFile) {
        $given = ''
        try { $given = ([IO.File]::ReadAllText($script:DemoDraftingFile)).Trim() } catch { $given = '' }
        if ($given -cin @('claude', 'gemini-flash', 'scripted')) {
            Add-Fact 'Drafting in the last start''s backend' (Get-ProviderLabel $given)
            if ($given -eq 'scripted') {
                Add-Notice 'The last start.cmd ran the backend as an offline rehearsal (Summarization__Provider=scripted): its notes are written by a script, not a model, and are not for an audience. To draft for real, run stop.cmd, then start.cmd from a new PowerShell window.'
            }
            elseif ($given -ne $provider) {
                Add-Notice ('The running backend was started to draft with ' + (Get-ProviderLabel $given) + ', but the saved choice is now ' + (Get-ProviderLabel $provider) + '. Run stop.cmd, then start.cmd, so the backend uses the saved choice and its key.')
            }
        }
        else {
            Add-Fact 'Drafting in the last start''s backend' 'unrecognised'
        }
    }
    else {
        Add-Fact 'Drafting in the last start''s backend' 'not recorded (start.cmd has not run since this was added)'
    }

    $model = $table[$script:KeyModel]
    if (-not $model) {
        $source = Join-Path $script:DemoApiDir 'Summarize\SummarizationOptions.cs'
        $model = 'unknown'
        if (Test-Path -LiteralPath $source) {
            $code = [IO.File]::ReadAllText($source)
            if ($provider -eq 'claude') { $constant = 'DefaultClaudeModel' } else { $constant = 'DefaultGeminiModel' }
            $match = [regex]::Match($code, $constant + ' = "([^"]+)"')
            if ($match.Success) { $model = $match.Groups[1].Value + ' (the default; not set)' }
        }
    }
    Add-Fact 'Drafting model' $model

    $key = ([string]$table[$keyName]).Trim()
    $keyLine = Get-YesNo $key
    if ($key) {
        $shape = Get-KeyShape $provider $key
        $keyLine += ", $shape"
        if ($shape.StartsWith('NOT')) {
            $label = Get-ProviderLabel $provider
            Add-Problem 22 "The saved drafting key does not look like a key for $label, which is the one chosen to draft. Every draft would be refused." "Run setup.cmd -NewKey and give it the $label key, or switch to the company the key belongs to with setup.cmd -Provider claude (or gemini)."
        }
    }
    Add-Fact "Drafting key configured ($keyName)" $keyLine
    $fallback = ([string]$table['Summarization:FallbackApiKey']).Trim()
    Add-Fact 'Fallback key configured' (Get-YesNo $fallback)
    Add-Fact 'Signing key configured' (Get-YesNo (Test-SigningKey $table[$script:KeySigning]))
    if ($table[$script:KeyDatabase]) {
        if ($script:Database) {
            $local = $script:Database.Host -match '^(localhost|127\.0\.0\.1|::1)$'
            Add-Fact 'Database connection saved' ("yes, to this laptop: $(Get-YesNo $local), port $($script:Database.Port)")
        }
        else {
            Add-Fact 'Database connection saved' 'yes, but it cannot be read as a connection string'
        }
    }
    else {
        Add-Fact 'Database connection saved' 'no'
    }

    $demoSetting = $table['Providers:Demo']
    if ($demoSetting) {
        if ($demoSetting -match '^(?i)true$') { $demoSetting = 'on' } else { $demoSetting = 'off' }
        Add-Fact 'Providers:Demo in user secrets' $demoSetting
    }
    else {
        Add-Fact 'Providers:Demo in user secrets' 'not set (off)'
    }
    if ($script:DemoLive -eq 'off') {
        Add-Notice 'The demo PSA (Providers:Demo) is off in the running backend, so Publish has no ticket to go to. That is expected unless you meant to show Publish (docs\dev\demo-psa.md).'
    }

    $missing = @()
    if (-not $key) { $missing += 'the drafting key' }
    if (-not (Test-SigningKey $table[$script:KeySigning])) { $missing += 'the signing key' }
    if (-not $table[$script:KeyDatabase]) { $missing += 'the database connection' }
    if ($missing.Count -gt 0) {
        Add-Problem 20 ('Setup has not been run, or did not finish. Missing: ' + ($missing -join ', ') + '.') 'Run scripts\windows\demo\setup.cmd.'
    }

    $names = @($table.Keys | Sort-Object)
    if ($names.Count -eq 0) { $names = @('none') }
    Add-Fact 'Names of all saved user secrets' ($names -join ', ')

    $pattern = '^(SCREENTAIL_|ASPNETCORE_|DOTNET_ENVIRONMENT$|Summarization__|ConnectionStrings__|Jwt__|Providers__|Vault__|Logging__)'
    foreach ($scope in @('User', 'Machine')) {
        $set = @([Environment]::GetEnvironmentVariables($scope).Keys | Where-Object { $_ -match $pattern } | Sort-Object)
        if ($set.Count -gt 0) { $line = $set -join ', ' } else { $line = 'none' }
        Add-Fact "ScreenTail-related variables ($scope)" $line
    }
}

Invoke-Section 'Device token' {
    $activated = Test-Path -LiteralPath $script:DemoDeviceCredential
    if ($activated) {
        Add-Fact 'Activated with an invite' "yes (device.cred written $(Get-FileTime $script:DemoDeviceCredential))"
    }
    else {
        Add-Fact 'Activated with an invite' 'no'
    }

    $serviceRunning = ($script:States['service'] -eq 'running')
    if (-not (Test-Path -LiteralPath $script:DemoTokenFile)) {
        Add-Fact 'Token start gave the service' 'not recorded (start.cmd from before this report, or it did not get that far)'
    }
    else {
        $record = ([IO.File]::ReadAllText($script:DemoTokenFile)).Trim()
        if ($record -eq 'activated') {
            Add-Fact 'Token start gave the service' 'none: an activated device renews its own'
        }
        elseif ($record -match '^development (\S+)$') {
            $until = $null
            try { $until = [DateTimeOffset]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) } catch { $until = $null }
            if ($until) {
                $left = $until - [DateTimeOffset]::Now
                if ($left.TotalSeconds -gt 0) {
                    Add-Fact 'Token start gave the service' ("a one-hour development token, present, good until " + $until.ToLocalTime().ToString('HH:mm') + " ($([int]$left.TotalMinutes) min left)")
                }
                else {
                    Add-Fact 'Token start gave the service' ("a one-hour development token that EXPIRED at " + $until.ToLocalTime().ToString('yyyy-MM-dd HH:mm'))
                    if ($serviceRunning) {
                        Add-Problem 75 ('The capture service''s one-hour development token ran out at ' + $until.ToLocalTime().ToString('HH:mm') + '. Drafts after that fail ("This device is not enrolled yet").') 'Run stop.cmd, then start.cmd. A session waiting to be drafted is drafted after the restart.'
                    }
                }
            }
            else {
                Add-Fact 'Token start gave the service' 'a development token whose expiry is unknown'
            }
        }
        else {
            Add-Fact 'Token start gave the service' 'not readable'
        }
    }

    foreach ($scope in @('User', 'Machine')) {
        $token = [Environment]::GetEnvironmentVariable('SCREENTAIL_DEVICE_TOKEN', $scope)
        if ($token) {
            $expiry = Get-TokenExpiry $token
            if ($expiry) { $when = 'expires ' + $expiry.ToLocalTime().ToString('yyyy-MM-dd HH:mm') } else { $when = 'not a JWT' }
            Add-Fact "SCREENTAIL_DEVICE_TOKEN ($scope)" "set ($when). The demo overrides it, but other programs see it."
        }
    }
}

Invoke-Section 'What Windows lets the capture service do' {
    $exe = Join-Path $script:DemoAppDir 'ScreenTail.Service.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        Add-Fact 'Capability check' 'not run: the capture service is not published yet (start.cmd publishes it)'
    }
    else {
        Add-Fact 'Published client' ("$script:DemoAppDir, built $(Get-FileTime $exe)")
        Add-CapabilityCheck -Exe $exe -WorkingDirectory $script:DemoAppDir
    }

    # OCR: the service asks Windows for an engine in the user's own languages, then English.
    try {
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
        $languages = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if ($engine) {
            $ocr = $engine.RecognizerLanguage.LanguageTag
        }
        elseif ($languages -contains 'en-US') {
            $ocr = 'en-US (the fallback)'
        }
        else {
            $ocr = 'none'
        }
        Add-Fact 'OCR language the service will use' $ocr
        Add-Fact 'OCR languages installed' ($languages -join ', ')
        if ($ocr -eq 'none') {
            Add-Notice 'Windows has no OCR language installed, so ScreenTail cannot read screenshots to find secrets in them. Add one: Settings > Time & language > Language & region > a language > Language options > Optical character recognition.'
        }
    }
    catch {
        Add-Fact 'OCR language the service will use' ('could not ask Windows (' + (Get-ExceptionTypeName $_) + ')')
    }

    # UI Automation: the service uses it to see whether the focused field is a password box.
    try {
        $type = [Type]::GetTypeFromCLSID([Guid]'ff48dba4-60ef-4201-aa87-54103eef594e')
        $automation = [Activator]::CreateInstance($type)
        if ($automation) {
            Add-Fact 'UI Automation available' 'yes'
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($automation)
        }
        else {
            Add-Fact 'UI Automation available' 'no'
        }
    }
    catch {
        Add-Fact 'UI Automation available' ('no (' + (Get-ExceptionTypeName $_) + ')')
        Add-Notice 'UI Automation is not available, so ScreenTail cannot tell when a password box has the cursor.'
    }

    # The speech model, by the name capture.json asks for or the default.
    $sizes = @{ 'tiny.en' = 77704715; 'base.en' = 147964211; 'small.en' = 487614201 }
    $capture = Read-JsonFile (Join-Path $env:LOCALAPPDATA 'ScreenTail\capture.json')
    $modelName = 'base.en'
    if ($capture -and $capture -ne 'unreadable' -and $capture.speech_model) { $modelName = [string]$capture.speech_model }
    $modelPath = Join-Path $env:LOCALAPPDATA ("ScreenTail\models\ggml-$modelName.bin")
    if (Test-Path -LiteralPath $modelPath) {
        $length = (New-Object IO.FileInfo($modelPath)).Length
        if ($sizes.ContainsKey($modelName) -and $length -ne $sizes[$modelName]) {
            Add-Fact "Speech model ($modelName)" "on disk but the wrong size ($length bytes); the service deletes and fetches it again"
        }
        else {
            Add-Fact "Speech model ($modelName)" "downloaded ($([Math]::Round($length / 1MB)) MB, $(Get-FileTime $modelPath))"
        }
    }
    elseif (Test-Path -LiteralPath ($modelPath + '.partial')) {
        Add-Fact "Speech model ($modelName)" 'downloading, or a download was cut off'
        Add-Notice 'The speech model has not finished downloading, so what you said is not in the note yet. Leave the demo running with the internet on; it carries on by itself.'
    }
    else {
        Add-Fact "Speech model ($modelName)" 'not downloaded'
        Add-Notice 'The speech model is not downloaded, so what you said is not in the note. The first session fetches it (about 150 MB) when the internet is on.'
    }
}

Invoke-Section 'Capture settings and hotkeys' {
    $dataDir = Join-Path $env:LOCALAPPDATA 'ScreenTail'
    $capture = Read-JsonFile (Join-Path $dataDir 'capture.json')
    $settings = Read-JsonFile (Join-Path $dataDir 'settings.json')
    $policy = Read-JsonFile (Join-Path $dataDir 'policy.json')
    if ($capture -eq 'unreadable') { Add-Fact 'capture.json' 'unreadable (the service uses the defaults)'; $capture = $null }
    if ($settings -eq 'unreadable') { Add-Fact 'settings.json' 'unreadable (the service uses the defaults)'; $settings = $null }
    if ($policy -eq 'unreadable') { Add-Fact 'policy.json' 'unreadable'; $policy = $null }

    $allMine = [bool]($capture -and $capture.capture_all_windows)
    $allPolicy = [bool]($policy -and $policy.captureAllWindows)
    if ($allMine -or $allPolicy) {
        $scope = 'All windows during a session'
        if ($allPolicy -and -not $allMine) { $scope += ' (set by the admin policy)' }
    }
    else {
        $scope = 'Remote-tool windows and admin tools only (the default)'
        Add-Notice 'Capture scope is remote-tool windows and admin tools only. If you ran the demo on the laptop alone (Ctrl+Alt+R), the Settings app and the print queue were not recorded: turn on All windows (docs\dev\demo.md, way B).'
    }
    Add-Fact 'Capture scope' $scope

    $autoStart = $true
    if ($capture -and $null -ne $capture.auto_start) { $autoStart = [bool]$capture.auto_start }
    Add-Fact 'Remote tool starts a session itself' (Get-YesNo $autoStart)
    $grace = 'the registry default (90 s)'
    if ($capture -and $capture.grace_seconds) { $grace = "$($capture.grace_seconds) s" }
    Add-Fact 'Grace after the remote tool goes' $grace
    $disabled = @()
    if ($capture -and $capture.disabled_tools) { $disabled = @($capture.disabled_tools) }
    if ($disabled.Count -gt 0) { Add-Fact 'Remote tools switched off' ($disabled -join ', ') } else { Add-Fact 'Remote tools switched off' 'none' }
    $sensitivity = 'medium'
    if ($capture -and $capture.sensitivity) { $sensitivity = $capture.sensitivity }
    Add-Fact 'Scene sensitivity' $sensitivity

    $defaults = [ordered]@{ 'StartCapture' = 'Ctrl+Alt+R'; 'PauseOrResume' = 'Ctrl+Alt+P'; 'StopAndDraft' = 'Ctrl+Alt+S'; 'MarkMoment' = 'Ctrl+Alt+M' }
    $chosen = @{}
    if ($capture -and $capture.hotkeys) {
        foreach ($property in $capture.hotkeys.PSObject.Properties) { $chosen[$property.Name] = [string]$property.Value }
    }
    foreach ($action in $defaults.Keys) {
        if ($chosen[$action]) { Add-Fact "Hotkey $action" "$($chosen[$action]) (changed in Settings)" } else { Add-Fact "Hotkey $action" "$($defaults[$action]) (default)" }
    }
    foreach ($action in @($chosen.Keys | Where-Object { -not $defaults.Contains($_) })) {
        Add-Fact "Hotkey $action" $chosen[$action]
    }

    $localMine = [bool]($settings -and $settings.local_only)
    $localPolicy = [bool]($policy -and $policy.localOnly)
    Add-Fact 'Local-only mode (Settings)' (Get-YesNo $localMine)
    if ($policy) {
        Add-Fact 'Admin policy cached' ("yes, version $($policy.version); local-only $(Get-YesNo $localPolicy), locked $(Get-YesNo $policy.localOnlyLocked)")
    }
    else {
        Add-Fact 'Admin policy cached' 'no'
    }
    if ($localMine -or $localPolicy) {
        Add-Problem 80 'Local-only mode is on, so sessions never go to the backend: notes are drafted on this laptop, plainer, not by the drafting model.' 'Right-click the tray icon > Open ScreenTail > Settings > Privacy, and turn Local-only mode off.'
    }
    if ($settings) {
        Add-Fact 'Retention' "$($settings.retention_days) days"
        Add-Fact 'Excluded apps (count)' (@($settings.excluded_processes | Where-Object { $_ }).Count)
        Add-Fact 'Custom redaction patterns (count)' (@($settings.custom_patterns | Where-Object { $_ }).Count)
    }
}

$demoLogs = @(
    @{ Name = 'Capture service'; Path = $script:DemoServiceLog; Rank = 90 },
    @{ Name = 'Backend'; Path = $script:DemoBackendLog; Rank = 92 }
)
Add-DemoLogsReport -Logs $demoLogs -OldBackendLog $script:DemoOldBackendLog -CrashDir (Join-Path $env:LOCALAPPDATA 'ScreenTail\crashes') -FilterSource $script:DemoLogWriterSource -FilterHash $script:DemoLogWriterHash

Add-E2eReport -Folder $script:DemoE2eDir -FilterSource $script:DemoLogWriterSource -FilterHash $script:DemoLogWriterHash

# --- Putting it together --------------------------------------------------------------------------------

# "What looks wrong" first, scrubbed now, so that what the scrub removed from it is counted below.
$wrong = New-Object System.Collections.Generic.List[string]
$sorted = @($script:Problems | Sort-Object -Property Rank, Order)
$firstText = $null
$firstFix = $null
if ($sorted.Count -eq 0) {
    $wrong.Add('  Nothing obviously wrong was found. In your message, say what you did and what you saw (what you')
    $wrong.Add('  pressed, what the pill or the tray said); the logs below have the rest.')
}
else {
    $firstText = Protect-ReportText $sorted[0].Text
    $firstFix = Protect-ReportText $sorted[0].Fix
    $wrong.Add('  FIRST PROBLEM: ' + $firstText)
    $wrong.Add('  FIX: ' + $firstFix)
    if ($sorted.Count -gt 1) {
        $wrong.Add('')
        $wrong.Add('  Also found, in order:')
        for ($i = 1; $i -lt $sorted.Count; $i++) {
            $wrong.Add('  - ' + (Protect-ReportText $sorted[$i].Text))
            $wrong.Add('    Fix: ' + (Protect-ReportText $sorted[$i].Fix))
        }
    }
}
if ($script:Notices.Count -gt 0) {
    $wrong.Add('')
    $wrong.Add('  Worth knowing:')
    foreach ($notice in $script:Notices) { $wrong.Add('  - ' + (Protect-ReportText $notice)) }
}

$header = New-Object System.Collections.Generic.List[string]
$header.Add('ScreenTail demo report')
$header.Add('Written ' + $now.ToString('yyyy-MM-dd HH:mm:ss') + ' local time (' + $now.ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC) by scripts\windows\demo\report.cmd.')
$header.Add('Apart from writing this file, it only read things; nothing else on this laptop was changed.')
$header.Add('')
$header.Add('What this report is made to leave out: screenshots, OCR text, transcripts, note text, window titles,')
$header.Add('keys, tokens, passwords and connection strings. Settings appear as yes/no or by name only. It never')
$header.Add('opens the session store. From the logs it keeps only the lines ScreenTail''s scrubbing logger wrote')
$header.Add('(INV-10); any other line a program printed, such as a .NET crash message or a native library''s')
$header.Add('output, is withheld and shown only as its length. Of any exception, logged or not, only the type and')
$header.Add('the stack frames are kept, never its message. Lines over 4,000 characters are shown only as their')
$header.Add('length. Every line here then went through a final scrub, which replaces: saved secrets by their exact')
$header.Add('value, secrets written as name=value, name: value or "name": "value", authorization headers, bearer')
$header.Add('and JWT tokens, AIza and sk- keys, passwords in connection strings and addresses, email addresses,')
$header.Add('long key-like strings, user folder paths (as %USERPROFILE%), the user name and the computer name,')
$header.Add('and anything shaped like the end-to-end run''s made-up secret. The end-to-end runner''s process logs')
$header.Add('go through the same filter; of its own e2e.log only the result, the start time and the ids of the')
$header.Add('steps that failed are shown, never what a step printed. A part of this report that could not be')
$header.Add('completed says so and names only the error''s type, and of git and dotnet only what they printed')
$header.Add('when they succeeded is shown, never their error output.')
$header.Add('Patterns can miss things, so read it before sending, as with anything.')
if ($script:Removed.Count -eq 0) {
    $header.Add('Nothing was withheld or removed.')
}
else {
    $header.Add('Withheld or removed:')
    foreach ($label in $script:Removed.Keys) {
        $header.Add("  - $($script:Removed[$label]) x $label")
    }
}
$header.Add('')
$header.Add('== What looks wrong')

$desktop = [Environment]::GetFolderPath('Desktop')
if (-not $desktop -or -not (Test-Path -LiteralPath $desktop)) { $desktop = Join-Path $env:USERPROFILE 'Desktop' }
$destination = Get-ReportFolder $desktop
if (-not (Test-Path -LiteralPath $destination.Folder)) { New-Item -ItemType Directory -Path $destination.Folder -Force | Out-Null }
$path = Join-Path $destination.Folder ('ScreenTail-report-' + $now.ToString('yyyyMMdd-HHmm') + '.txt')
$all = @($header) + @($wrong) + @($script:Body)
[IO.File]::WriteAllLines($path, [string[]]$all, (New-Object Text.UTF8Encoding $true))

Write-Host ''
if ($firstText) {
    Write-Host ('First problem: ' + $firstText) -ForegroundColor Yellow
    Write-Host ('Fix: ' + $firstFix) -ForegroundColor Yellow
    Write-Host ''
}
if ($destination.Synced) {
    Write-Host 'Your Desktop is synced to OneDrive, so the report was NOT put there (OneDrive would upload it at' -ForegroundColor Green
    Write-Host 'once). It is in this folder, which opens now:' -ForegroundColor Green
}
else {
    Write-Host 'The report is on your Desktop:' -ForegroundColor Green
}
Write-Host "  $path" -ForegroundColor Green
Write-Host 'Read it if you like (it opens in Notepad now), then send that one file to the developer.'
try { Start-Process -FilePath 'notepad.exe' -ArgumentList @("`"$path`"") } catch { }
if ($destination.Synced) {
    try { Start-Process -FilePath 'explorer.exe' -ArgumentList @("/select,`"$path`"") } catch { }
}
exit 0
