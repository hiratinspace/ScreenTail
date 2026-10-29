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
    At the top it says what looks wrong first, with a one-line fix.

    It only reads. It starts nothing that stays running, stops nothing and saves nothing except the
    report itself. The one program it runs is the capture service's own "--capabilities" check, which
    asks Windows what it allows and exits.

    It is made to leave out screenshots, OCR text, transcripts, note text, window titles, keys, tokens,
    passwords and connection strings. Of the logs it keeps only the lines ScreenTail's scrubbing log
    sink wrote (INV-10): any other line a program printed (a .NET crash message, native library output)
    is withheld and shown only as its length, and of an unhandled exception only the type is kept. Every
    line of the report then goes through a final pattern scrub. The top of the report says what was
    withheld and removed.

.EXAMPLE
    scripts\windows\demo\report.cmd
#>
[CmdletBinding()]
param()

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
$script:LineCut = 4000
$passwordNames = 'password|passwd|pwd|psw'
$secretNames = 'secret|token|credential|api[_\-]?key|signing[_\-]?key|access[_\-]?key|master[_\-]?key|private[_\-]?key|public[_\-]?key|client[_\-]?secret|device[_\-]?token|refresh[_\-]?token'
$quoted = '"(?:[^"\\]|\\.)*"|''[^'']*'''
$nameStart = '(?<![A-Za-z0-9_.:\-])'
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
    @{ Label = 'email addresses'; Pattern = '(?<![A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}'; Replacement = '[email]' }
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

# Runs one section. A section that fails says so in the report and the rest still runs.
function Invoke-Section([string]$Title, [scriptblock]$Body) {
    Write-Host "  $Title"
    Add-Line ''
    Add-Line ('== ' + $Title)
    try {
        & $Body
    }
    catch {
        Add-Line ('  (This part could not be checked: ' + $_.Exception.Message + ')')
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

# ConvertTo-DemoLogLine, the filter start's windows write their logs through.
. ([scriptblock]::Create($script:DemoLogWriterSource))

# Lines read from a log or a program's output, with every line that ScreenTail's scrubbing logger did not
# write withheld, the same way the windows withhold them as they write. Again here because a file from an
# older start, or a program's output read directly, was never filtered.
function Select-DemoLogLines([string[]]$Lines) {
    $state = @{ InLogger = $false; Reading = $true }
    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($line in $Lines) {
        $text = ConvertTo-DemoLogLine $line $state
        if ($text -match '^\[line not from ScreenTail''s logger withheld, \d+ chars\]$|^Unhandled exception\. \S+ \[message and stack withheld\]$') {
            Add-Removed 'lines not written by ScreenTail''s logger (shown only as their length, or an exception''s type)'
        }
        $kept.Add($text)
    }
    return $kept.ToArray()
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

# The last lines of a log, filtered, each indented, after a line saying how many there are.
function Add-LogTail([string]$Path, [int]$Count) {
    $lines = @(Select-DemoLogLines @(Read-SharedLog $Path))
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

Invoke-Section 'The .NET SDK' {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Add-Fact '.NET SDK' 'dotnet is not installed, or not on PATH'
        Add-Problem 10 'dotnet is not installed, or not on PATH, so nothing of the demo can be built.' 'Install the .NET 10 SDK (winget install --id Microsoft.DotNet.SDK.10 -e --source winget), then open a new PowerShell window.'
        $script:HaveDotnet = $false
        return
    }
    $script:HaveDotnet = $true
    $version = Invoke-Native -FilePath 'dotnet' -ArgumentList @('--version') -WorkingDirectory $script:DemoRepo
    $chosen = ($version.Lines | Select-Object -Last 1)
    if ($version.ExitCode -eq 0 -and $chosen -match '^10\.') {
        Add-Fact '.NET SDK this clone uses' $chosen
    }
    else {
        Add-Fact '.NET SDK this clone uses' 'none that global.json accepts'
        Add-Problem 10 'No .NET 10 SDK that this repository accepts is installed.' 'Install it (winget install --id Microsoft.DotNet.SDK.10 -e --source winget), then open a new PowerShell window.'
    }
    $sdks = Invoke-Native -FilePath 'dotnet' -ArgumentList @('--list-sdks')
    $installed = @($sdks.Lines | ForEach-Object { ($_ -split ' ')[0] } | Where-Object { $_ })
    Add-Fact '.NET SDKs installed' ($installed -join ', ')
}

Invoke-Section 'The clone' {
    Add-Fact 'Folder' $script:DemoRepo
    if ($script:DemoRepo -match '(?i)actions-runner|\\_work\\') {
        Add-Notice 'The demo is running from the CI runner''s folder. Use your own clone (for example C:\src\ScreenTail): the runner changes its folder under you.'
    }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Add-Fact 'git' 'not installed or not on PATH, so the commit is unknown'
        return
    }
    $branch = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $script:DemoRepo, 'rev-parse', '--abbrev-ref', 'HEAD')
    Add-Fact 'Branch' (($branch.Lines | Select-Object -First 1))
    # The commit's subject only when origin has the commit, so it is already public. A commit made only on
    # this laptop is shown by its id and date.
    $published = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $script:DemoRepo, 'branch', '-r', '--contains', 'HEAD')
    $onOrigin = ($published.ExitCode -eq 0 -and @($published.Lines | Where-Object { $_ -match '^\s*origin/' }).Count -gt 0)
    if ($onOrigin) { $format = '--format=%H %cd %s' } else { $format = '--format=%H %cd (not on origin; subject not shown)' }
    $head = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $script:DemoRepo, 'log', '-1', $format, '--date=short')
    Add-Fact 'Commit' (($head.Lines | Select-Object -First 1))

    $compare = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $script:DemoRepo, 'rev-list', '--left-right', '--count', 'origin/main...HEAD')
    $counts = (($compare.Lines | Select-Object -First 1) -split '\s+')
    if ($compare.ExitCode -eq 0 -and $counts.Count -ge 2) {
        Add-Fact 'Against origin/main (last fetch)' ("$($counts[0]) behind, $($counts[1]) ahead")
        if ([int]$counts[0] -gt 0) {
            Add-Notice "The clone is $($counts[0]) commit(s) behind origin/main as of its last fetch. A 'git pull' may already fix what went wrong."
        }
    }

    # Changed files the repository tracks, by name. Untracked files are only counted: their names are the
    # owner's, not the project's. --no-optional-locks: status does not even refresh git's index file.
    $status = Invoke-Native -FilePath 'git' -ArgumentList @('--no-optional-locks', '-C', $script:DemoRepo, 'status', '--porcelain', '-uno')
    $changed = @($status.Lines | Where-Object { $_.Trim() })
    if ($changed.Count -eq 0) {
        Add-Fact 'Changed tracked files' 'none'
    }
    else {
        Add-Fact 'Changed tracked files' "$($changed.Count):"
        foreach ($line in ($changed | Select-Object -First 20)) { Add-Line ('    ' + $line) }
        if ($changed.Count -gt 20) { Add-Line "    ... and $($changed.Count - 20) more" }
        Add-Notice "The clone has $($changed.Count) changed file(s) (listed under 'The clone'). The developer may ask you to undo them."
    }
    $untracked = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $script:DemoRepo, 'ls-files', '--others', '--exclude-standard')
    Add-Fact 'Untracked files (count only)' (@($untracked.Lines | Where-Object { $_.Trim() }).Count)
}

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
            $reason = ((Read-SharedLog $file) -join ' ').Trim()
            Add-Fact ([IO.Path]::GetFileName($file)) ("$reason (written $(Get-FileTime $file))")
            if ($reason -match 'Application Control|blocked by|group policy') {
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
        $run = $null
        try {
            $run = Invoke-WithTimeout -FilePath $exe -Arguments '--capabilities' -WorkingDirectory $script:DemoAppDir -Seconds 60
        }
        catch {
            $reason = $_.Exception.Message
            Add-Fact 'Capability check' "could not run: $reason"
            if ($reason -match 'Application Control|blocked by|group policy') {
                Add-Problem 12 'Windows blocks the capture service from running (an Application Control policy). Fresh unsigned builds trip it on this laptop.' 'Allow it in Windows Security > App & browser control, or ask the developer (ST-112 is the signing fix).'
            }
        }
        if ($run -and $run.TimedOut) {
            Add-Fact 'Capability check' 'did not finish within 60 seconds'
        }
        elseif ($run) {
            $text = $run.Out
            $at = $text.IndexOf('{')
            $report = $null
            if ($at -ge 0) {
                try { $report = $text.Substring($at) | ConvertFrom-Json } catch { $report = $null }
            }
            if (-not $report) {
                Add-Fact 'Capability check' "exit code $($run.ExitCode), no report"
                # Only what the service's logger or its fixed sentences say; a crash's message is withheld.
                $said = @(Select-DemoLogLines @((($run.Err + "`n" + $run.Out) -split '\r\n|\r|\n') | Where-Object { $_.Trim() }))
                foreach ($line in @($said | Select-Object -First 10)) { Add-Line ('    ' + $line) }
            }
            else {
                Add-Fact 'Can capture' (Get-YesNo $report.can_capture)
                foreach ($check in @($report.checks)) {
                    $line = "$($check.state). $($check.message)"
                    if ($check.fix_hint) { $line += " Fix: $($check.fix_hint)" }
                    Add-Fact $check.capability $line
                }
                $blocked = @($report.checks | Where-Object { $_.state -ne 'ok' -and $_.capability -ne 'elevated_windows' })
                if (-not $report.can_capture) {
                    $first = @($report.checks | Where-Object { $_.state -eq 'blocked' -or $_.state -eq 'unknown' }) | Select-Object -First 1
                    $fix = 'See "What Windows lets the capture service do" below.'
                    if ($first -and $first.fix_hint) { $fix = $first.fix_hint }
                    $what = 'one of its checks failed'
                    if ($first) { $what = "$($first.capability): $($first.message)" }
                    Add-Problem 85 "Windows is not letting ScreenTail capture ($what)." $fix
                }
                foreach ($check in $blocked) {
                    if ($report.can_capture) {
                        $hint = ''
                        if ($check.fix_hint) { $hint = " Fix: $($check.fix_hint)" }
                        Add-Notice "$($check.capability) is $($check.state): $($check.message)$hint"
                    }
                }
                $said = @(Select-DemoLogLines @(($run.Err -split '\r\n|\r|\n') | Where-Object { $_.Trim() }))
                foreach ($line in @($said | Select-Object -First 5)) { Add-Line ('    stderr: ' + $line) }
            }
        }
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
        Add-Fact 'OCR language the service will use' ('could not ask Windows: ' + $_.Exception.Message)
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
        Add-Fact 'UI Automation available' ('no: ' + $_.Exception.Message)
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

Invoke-Section ("Logs (the last $script:LogTail lines of each; only the logger's lines are kept)") {
    $logs = @(
        @{ Name = 'Capture service'; Path = $script:DemoServiceLog; Rank = 90 },
        @{ Name = 'Backend'; Path = $script:DemoBackendLog; Rank = 92 }
    )
    foreach ($log in $logs) {
        Add-Line ''
        Add-Line ("  -- $($log.Name)")
        $lines = @()
        if (Test-Path -LiteralPath $log.Path) {
            $lines = @(Add-LogTail -Path $log.Path -Count $script:LogTail)
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

    if (Test-Path -LiteralPath $script:DemoOldBackendLog) {
        Add-Line ''
        Add-Line '  -- Backend, from an older start.cmd (before the logs folder)'
        $null = Add-LogTail -Path $script:DemoOldBackendLog -Count $script:LogTail
    }

    Add-Line ''
    Add-Line '  -- UI'
    Add-Line '  The UI writes no log file: it has no console window, and its only record is a crash report,'
    Add-Line '  which is written only when SCREENTAIL_CRASH_REPORTS=1 is set.'
    $crashDir = Join-Path $env:LOCALAPPDATA 'ScreenTail\crashes'
    $crashes = @()
    if (Test-Path -LiteralPath $crashDir) {
        $crashes = @(Get-ChildItem -LiteralPath $crashDir -Filter '*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    }
    Add-Fact 'Crash reports (service and UI)' $crashes.Count
    if ($crashes.Count -gt 0) {
        Add-Line ('  The newest, ' + $crashes[0].Name + ' (exception types and stack frames only):')
        foreach ($line in @(Read-SharedLog $crashes[0].FullName | Select-Object -First 60)) { Add-Line ('    ' + $line) }
    }
}

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
$header.Add('long key-like strings, user folder paths (as %USERPROFILE%), the user name and the computer name.')
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
